using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using RosterControl.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace RosterControl.Integrations;

/// <summary>
/// Downloads public images (Data Dragon / Community Dragon) once and serves them from disk afterwards.
/// <list type="bullet">
/// <item>Patch-versioned URLs (<c>/cdn/16.23.1/img/…</c>: portraits, items, spells) never change, so they are cached
/// forever; a new patch uses new URLs. Old patches are pruned.</item>
/// <item>Unversioned URLs (splash art, rune and shard icons, role icons) can change when Riot updates art, so they are
/// revalidated after <see cref="UnversionedMaxAge"/> with a conditional request (ETag / Last-Modified). The cached copy
/// is always served immediately; an updated file is picked up the next time the image is loaded.</item>
/// </list>
/// </summary>
public sealed partial class AssetCache : IAssetCache
{
    public static readonly TimeSpan UnversionedMaxAge = TimeSpan.FromDays(7);

    private readonly HttpClient _http;
    private readonly ILogger<AssetCache> _logger;
    private readonly string _root;
    private readonly ConcurrentDictionary<string, Lazy<Task<string?>>> _inflight = new();
    private readonly ConcurrentDictionary<string, byte> _missing = new();
    private readonly ConcurrentDictionary<string, byte> _revalidating = new();
    private readonly SemaphoreSlim _concurrency = new(6, 6);

    public AssetCache(HttpClient http, IAppPaths paths, ILogger<AssetCache> logger)
    {
        _http = http;
        _logger = logger;
        _http.Timeout = TimeSpan.FromSeconds(30);
        _root = Path.Combine(paths.CacheDirectory, "images");
        Directory.CreateDirectory(_root);
    }

    [GeneratedRegex(@"/cdn/(\d+\.\d+\.\d+)/", RegexOptions.CultureInvariant)]
    private static partial Regex VersionedPath();

    /// <summary>True for URLs that include a Data Dragon patch version (their content never changes).</summary>
    public static bool IsVersioned(string url) => VersionedPath().IsMatch(url);

    public string? TryGetCachedPath(string url)
    {
        var path = LocalPath(url);
        if (path is null || !File.Exists(path)) return null;
        if (!IsVersioned(url) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > UnversionedMaxAge)
            ScheduleRevalidation(url, path);
        return path;
    }

    public Task<string?> GetAsync(string url, CancellationToken cancellationToken)
    {
        var local = LocalPath(url);
        if (local is null) return Task.FromResult<string?>(null);
        if (File.Exists(local)) return Task.FromResult(TryGetCachedPath(url));
        if (_missing.ContainsKey(url)) return Task.FromResult<string?>(null);

        var lazy = _inflight.GetOrAdd(local, _ => new Lazy<Task<string?>>(() => DownloadAsync(url, local, revalidate: false)));
        return lazy.Value.WaitAsync(cancellationToken);
    }

    /// <summary>Deletes cached Data Dragon images of patches not in <paramref name="keep"/>.</summary>
    public int PruneVersions(IReadOnlyCollection<string> keep)
    {
        var cdn = Path.Combine(_root, "ddragon.leagueoflegends.com", "cdn");
        if (!Directory.Exists(cdn)) return 0;
        var removed = 0;
        foreach (var dir in Directory.EnumerateDirectories(cdn))
        {
            var name = Path.GetFileName(dir);
            if (name.Length == 0 || !char.IsDigit(name[0]) || keep.Contains(name)) continue;
            try
            {
                Directory.Delete(dir, recursive: true);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug("Could not prune {Dir}: {Message}", dir, ex.Message);
            }
        }
        return removed;
    }

    private void ScheduleRevalidation(string url, string local)
    {
        if (!_revalidating.TryAdd(url, 0)) return; // once per session per URL
        _ = Task.Run(() => DownloadAsync(url, local, revalidate: true));
    }

    private async Task<string?> DownloadAsync(string url, string local, bool revalidate)
    {
        await _concurrency.WaitAsync().ConfigureAwait(false);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            var meta = revalidate ? ReadMeta(local) : null;
            if (meta?.ETag is { Length: > 0 } etag && EntityTagHeaderValue.TryParse(etag, out var tag)) request.Headers.IfNoneMatch.Add(tag);
            if (meta?.LastModified is { } modified) request.Headers.IfModifiedSince = modified;

            using var response = await _http.SendAsync(request).ConfigureAwait(false);
            if (revalidate && response.StatusCode == HttpStatusCode.NotModified)
            {
                File.SetLastWriteTimeUtc(local, DateTime.UtcNow);
                return local;
            }
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Image {Url} returned {Status}", url, (int)response.StatusCode);
                if (!revalidate && (int)response.StatusCode is 403 or 404) _missing[url] = 0;
                if (revalidate) File.SetLastWriteTimeUtc(local, DateTime.UtcNow); // keep the cached copy; try again later
                return revalidate ? local : null;
            }
            var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            Directory.CreateDirectory(Path.GetDirectoryName(local)!);
            var tmp = local + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllBytesAsync(tmp, bytes).ConfigureAwait(false);
            File.Move(tmp, local, overwrite: true);
            if (!IsVersioned(url)) WriteMeta(local, new ImageMeta(response.Headers.ETag?.ToString(), response.Content.Headers.LastModified));
            if (revalidate) _logger.LogInformation("Updated cached image {Url}", url);
            return local;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            _logger.LogDebug("Image download failed for {Url}: {Message}", url, ex.Message);
            return revalidate ? local : null;
        }
        finally
        {
            _concurrency.Release();
            if (!revalidate) _inflight.TryRemove(local, out _);
        }
    }

    private sealed record ImageMeta(string? ETag, DateTimeOffset? LastModified);

    private static ImageMeta? ReadMeta(string local)
    {
        try
        {
            var path = local + ".meta";
            return File.Exists(path) ? JsonSerializer.Deserialize<ImageMeta>(File.ReadAllText(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }

    private static void WriteMeta(string local, ImageMeta meta)
    {
        try
        {
            File.WriteAllText(local + ".meta", JsonSerializer.Serialize(meta));
        }
        catch (IOException)
        {
        }
    }

    internal string? LocalPath(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")) return null;
        var invalid = Path.GetInvalidFileNameChars();
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => string.Concat(Uri.UnescapeDataString(s).Select(c => invalid.Contains(c) ? '_' : c)))
            .Where(s => s is not ("." or ".."))
            .ToList();
        if (segments.Count == 0) return null;
        return Path.Combine([_root, uri.Host, .. segments]);
    }
}
