using System.Text.Json;
using RosterControl.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace RosterControl.Integrations;

/// <summary>
/// HTTP + disk cache client for the keyless public sources: Data Dragon, Community Dragon and Meraki Analytics.
/// <list type="bullet">
/// <item>The patch list is always checked online first (short timeout) so a new patch is never missed; the cached copy is
/// used when offline.</item>
/// <item>Versioned Data Dragon files never change once published, so they are cached forever (per patch) and old patches
/// are pruned.</item>
/// <item>"Latest" documents (Community Dragon, Meraki) are refreshed periodically, when the patch changes, or when they
/// are missing a champion.</item>
/// </list>
/// </summary>
public sealed class GameDataClient
{
    public static readonly Uri DataDragonBase = new("https://ddragon.leagueoflegends.com/");
    public static readonly Uri CommunityDragonBase = new("https://raw.communitydragon.org/latest/plugins/rcp-be-lol-game-data/global/default/v1/");
    public static readonly Uri MerakiChampions = new("https://cdn.merakianalytics.com/riot/lol/resources/latest/en-US/champions.json");
    public const string Locale = "en_US";

    private static readonly TimeSpan VersionCheckTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan CommunityDragonMaxAge = TimeSpan.FromDays(1);
    private static readonly TimeSpan PositionsMaxAge = TimeSpan.FromDays(7);
    /// <summary>When champions are missing from the positions map (a new champion), retry at most this often.</summary>
    private static readonly TimeSpan PositionsMissingRetry = TimeSpan.FromHours(12);

    private readonly HttpClient _http;
    private readonly ILogger<GameDataClient> _logger;
    private readonly string _cacheRoot;

    public GameDataClient(HttpClient http, IAppPaths paths, ILogger<GameDataClient> logger)
    {
        _http = http;
        _logger = logger;
        _http.Timeout = TimeSpan.FromSeconds(60);
        _cacheRoot = Path.Combine(paths.CacheDirectory, "data");
        Directory.CreateDirectory(_cacheRoot);
    }

    public string CacheRoot => _cacheRoot;

    /// <summary>
    /// versions.json (newest first), checked online first; falls back to the cached copy when offline
    /// (<c>FromCache</c> is then true).
    /// </summary>
    public async Task<(IReadOnlyList<string> Versions, bool FromCache)> GetVersionsAsync(CancellationToken ct)
    {
        var (doc, fromCache) = await GetCachedCoreAsync(new Uri(DataDragonBase, "api/versions.json"), Path.Combine(_cacheRoot, "versions.json"),
            TimeSpan.Zero, ct, VersionCheckTimeout).ConfigureAwait(false);
        using (doc)
        {
            var versions = doc.RootElement.EnumerateArray().Select(e => e.GetString()).Where(s => !string.IsNullOrEmpty(s) && char.IsDigit(s[0])).Cast<string>().ToList();
            return (versions, fromCache);
        }
    }

    /// <summary>A versioned Data Dragon data file (immutable once published), e.g. "champion.json" or "champion/Ahri.json".</summary>
    public Task<JsonDocument> GetDataAsync(string version, string file, CancellationToken ct) =>
        GetCachedAsync(new Uri(DataDragonBase, $"cdn/{version}/data/{Locale}/{file}"), Path.Combine(_cacheRoot, version, Locale, file), maxAge: null, ct);

    /// <summary>Community Dragon game data (perks.json, perkstyles.json): stat shards and rune page art not in Data Dragon.</summary>
    public Task<JsonDocument> GetCommunityDragonAsync(string file, bool forceRefresh, CancellationToken ct) =>
        GetCachedAsync(new Uri(CommunityDragonBase, file), Path.Combine(_cacheRoot, "cdragon", file), forceRefresh ? TimeSpan.Zero : CommunityDragonMaxAge, ct);

    public bool IsVersionCached(string version) =>
        new[] { "champion.json", "item.json", "runesReforged.json", "summoner.json" }.All(f => File.Exists(Path.Combine(_cacheRoot, version, Locale, f)));

    public IEnumerable<string> CachedVersions() =>
        Directory.Exists(_cacheRoot)
            ? Directory.EnumerateDirectories(_cacheRoot).Select(Path.GetFileName).Where(n => n is not null && char.IsDigit(n[0])).Cast<string>().Where(IsVersionCached)
            : [];

    /// <summary>Deletes cached data of patches not in <paramref name="keep"/>. Returns the removed versions.</summary>
    public IReadOnlyList<string> PruneVersions(IReadOnlyCollection<string> keep)
    {
        var removed = new List<string>();
        if (!Directory.Exists(_cacheRoot)) return removed;
        foreach (var dir in Directory.EnumerateDirectories(_cacheRoot))
        {
            var name = Path.GetFileName(dir);
            if (name.Length == 0 || !char.IsDigit(name[0]) || keep.Contains(name)) continue;
            try
            {
                Directory.Delete(dir, recursive: true);
                removed.Add(name);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug("Could not prune {Dir}: {Message}", dir, ex.Message);
            }
        }
        return removed;
    }

    /// <summary>Small persisted markers, e.g. which patch the Community Dragon documents were fetched for.</summary>
    public string? ReadMarker(string name)
    {
        var path = Path.Combine(_cacheRoot, name + ".marker");
        try
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public void WriteMarker(string name, string value)
    {
        try
        {
            File.WriteAllText(Path.Combine(_cacheRoot, name + ".marker"), value);
        }
        catch (IOException ex)
        {
            _logger.LogDebug("Could not write marker {Name}: {Message}", name, ex.Message);
        }
    }

    /// <summary>
    /// Champion → positions (TOP, JUNGLE, ...) from Meraki Analytics. The source document is large (~13 MB), so only the
    /// derived mapping is cached (7 days). It is refreshed sooner when <paramref name="championIds"/> contains a champion
    /// it doesn't know (a newly released champion). Returns an empty map if unavailable; positions are optional.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> GetPositionsAsync(bool forceRefresh, IReadOnlyCollection<string> championIds, CancellationToken ct)
    {
        var cachePath = Path.Combine(_cacheRoot, "positions.json");
        var exists = File.Exists(cachePath);
        var cached = exists ? await TryReadPositionsAsync(cachePath, ct).ConfigureAwait(false) : null;
        if (cached is not null && !forceRefresh)
        {
            var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(cachePath);
            var missing = championIds.Where(id => !cached.ContainsKey(id)).ToList();
            if (age < PositionsMaxAge && (missing.Count == 0 || age < PositionsMissingRetry)) return cached;
            if (missing.Count > 0) _logger.LogInformation("Refreshing champion positions; missing {Champions}", string.Join(", ", missing));
        }
        try
        {
            using var response = await _http.GetAsync(MerakiChampions, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            var map = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var champ in doc.RootElement.EnumerateObject())
            {
                if (!champ.Value.TryGetProperty("positions", out var positions) || positions.ValueKind != JsonValueKind.Array) continue;
                map[champ.Name] = positions.EnumerateArray().Select(p => p.GetString() ?? "").Where(p => p.Length > 0).ToList();
            }
            var tmp = cachePath + ".tmp";
            await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(map), ct).ConfigureAwait(false);
            File.Move(tmp, cachePath, overwrite: true); // also restarts the retry clock when Meraki hasn't caught up yet
            return map;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException && !ct.IsCancellationRequested)
        {
            _logger.LogWarning("Champion positions unavailable ({Message}); using cached copy if present", ex.Message);
            return cached ?? new Dictionary<string, IReadOnlyList<string>>();
        }
    }

    private static async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>?> TryReadPositionsAsync(string path, CancellationToken ct)
    {
        try
        {
            await using var fs = File.OpenRead(path);
            var map = await JsonSerializer.DeserializeAsync<Dictionary<string, List<string>>>(fs, cancellationToken: ct).ConfigureAwait(false);
            return map?.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }

    /// <param name="maxAge">null = immutable (cache forever); otherwise re-download when the cached copy is older.</param>
    /// <param name="networkTimeout">Optional shorter timeout for the network attempt before falling back to the cache.</param>
    private async Task<JsonDocument> GetCachedAsync(Uri uri, string cachePath, TimeSpan? maxAge, CancellationToken ct) =>
        (await GetCachedCoreAsync(uri, cachePath, maxAge, ct, null).ConfigureAwait(false)).Doc;

    private async Task<(JsonDocument Doc, bool FromCache)> GetCachedCoreAsync(Uri uri, string cachePath, TimeSpan? maxAge, CancellationToken ct, TimeSpan? networkTimeout)
    {
        var exists = File.Exists(cachePath);
        var fresh = exists && (maxAge is null || DateTime.UtcNow - File.GetLastWriteTimeUtc(cachePath) < maxAge);
        if (fresh)
        {
            try
            {
                return (await ReadAsync(cachePath, ct).ConfigureAwait(false), true);
            }
            catch (JsonException)
            {
                _logger.LogWarning("Corrupt cache file {Path}; re-downloading", cachePath);
            }
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (networkTimeout is { } t && exists) timeout.CancelAfter(t); // only shorten when there is a fallback
            using var response = await _http.GetAsync(uri, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
            var doc = JsonDocument.Parse(bytes); // validate before caching
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            var tmp = cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllBytesAsync(tmp, bytes, ct).ConfigureAwait(false);
            File.Move(tmp, cachePath, overwrite: true);
            return (doc, false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or JsonException && !ct.IsCancellationRequested)
        {
            if (!exists) throw;
            _logger.LogWarning("Could not refresh {Uri} ({Message}); using cached copy", uri, ex.Message);
            return (await ReadAsync(cachePath, ct).ConfigureAwait(false), true);
        }
    }

    private static async Task<JsonDocument> ReadAsync(string path, CancellationToken ct)
    {
        await using var fs = File.OpenRead(path);
        return await JsonDocument.ParseAsync(fs, cancellationToken: ct).ConfigureAwait(false);
    }
}
