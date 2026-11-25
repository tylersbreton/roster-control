using System.Collections.Concurrent;
using System.Text.Json;
using RosterControl.Core.Abstractions;
using RosterControl.Core.GameData;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace RosterControl.Integrations;

/// <summary>
/// Loads the newest Data Dragon patch (falling back to the newest cached patch when offline) together with Community
/// Dragon stat shards and Meraki champion positions, and exposes it as one immutable snapshot.
/// <para>Caching: patch data is immutable and cached per patch (the newest <see cref="PatchesKept"/> patches are kept);
/// Community Dragon documents are refreshed whenever the patch changes; positions are refreshed when a champion is
/// missing from them (new champions); the patch list itself is checked online at every start and periodically.</para>
/// </summary>
public sealed class GameDataService : IGameDataService
{
    /// <summary>Patches whose data and images stay cached (current + previous ones, for builds saved on older patches).</summary>
    public const int PatchesKept = 3;

    private readonly GameDataClient _client;
    private readonly IAssetCache _assets;
    private readonly ILogger<GameDataService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, Task<ChampionAbilities?>> _abilities = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task<IReadOnlyDictionary<string, ItemInfo>?>> _itemsByVersion = new();

    public GameDataService(GameDataClient client, IAssetCache assets, ILogger<GameDataService> logger)
    {
        _client = client;
        _assets = assets;
        _logger = logger;
    }

    public GameDataSnapshot? Current { get; private set; }
    public GameDataStatus? Status { get; private set; }

    public async Task<GameDataSnapshot> LoadAsync(bool forceRefresh, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Always re-checks the patch list; forceRefresh additionally re-downloads the "latest" documents
            // (Community Dragon, Meraki) that are otherwise refreshed on a schedule or when the patch changes.
            var warnings = new List<string>();
            var source = GameDataSource.Online;

            progress?.Report("Checking the latest patch…");
            IReadOnlyList<string> versions;
            try
            {
                (versions, var fromCache) = await _client.GetVersionsAsync(cancellationToken).ConfigureAwait(false);
                if (fromCache)
                {
                    source = GameDataSource.Cache;
                    warnings.Add("Offline: couldn't check for a new patch; using cached game data.");
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or JsonException && !cancellationToken.IsCancellationRequested)
            {
                versions = [];
                _logger.LogWarning("Patch list unavailable: {Message}", ex.Message);
            }

            var version = versions.FirstOrDefault() ?? NewestCachedVersion() ?? throw new GameDataUnavailableException(
                "Could not reach Data Dragon and no game data is cached yet. Connect to the internet and try again.");
            if (versions.Count == 0)
            {
                source = GameDataSource.Cache;
                warnings.Add("Offline: using cached game data.");
            }

            GameDataSnapshot snapshot;
            try
            {
                snapshot = await LoadVersionAsync(version, versions, forceRefresh, warnings, progress, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                // A new patch was announced but could not be downloaded: fall back to the newest complete cached patch.
                var cached = NewestCachedVersion();
                if (cached is null || cached == version)
                    throw new GameDataUnavailableException("Could not download game data from Data Dragon. Check your connection and try again.", ex);
                _logger.LogWarning(ex, "Could not download {Version}; using cached {Cached}", version, cached);
                warnings.Add($"Could not download patch {version}; using cached {cached}.");
                source = GameDataSource.Cache;
                snapshot = await LoadVersionAsync(cached, versions, false, warnings, progress, cancellationToken).ConfigureAwait(false);
            }

            Current = snapshot;
            Status = new GameDataStatus(snapshot.Version, source, DateTimeOffset.Now, warnings);
            _abilities.Clear();
            _logger.LogInformation("Game data {Version} loaded ({Source}): {Champions} champions ({New} new), {Items} shop items, {Trees} rune paths, {Shards} shard rows",
                snapshot.Version, source, snapshot.Champions.Count, snapshot.NewChampionIds.Count, snapshot.ShopItems.Count, snapshot.RuneTrees.Count, snapshot.ShardRows.Count);
            if (source == GameDataSource.Online) Prune(versions, snapshot.Version);
            return snapshot;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<GameDataSnapshot> LoadVersionAsync(string version, IReadOnlyList<string> versions, bool forceRefresh, List<string> warnings,
        IProgress<string>? progress, CancellationToken ct)
    {
        progress?.Report($"Loading champions (patch {version})…");
        using var champions = await _client.GetDataAsync(version, "champion.json", ct).ConfigureAwait(false);
        progress?.Report("Loading items…");
        using var items = await _client.GetDataAsync(version, "item.json", ct).ConfigureAwait(false);
        progress?.Report("Loading runes…");
        using var runes = await _client.GetDataAsync(version, "runesReforged.json", ct).ConfigureAwait(false);
        using var spells = await _client.GetDataAsync(version, "summoner.json", ct).ConfigureAwait(false);
        var championIds = ChampionIds(champions);

        // Stat shards / rune art change with patches: refresh them whenever the patch changes (otherwise daily).
        JsonDocument? perkStyles = null, perks = null;
        try
        {
            progress?.Report("Loading stat shards…");
            var patchChanged = _client.ReadMarker("cdragon-patch") != version;
            perkStyles = await _client.GetCommunityDragonAsync("perkstyles.json", forceRefresh || patchChanged, ct).ConfigureAwait(false);
            perks = await _client.GetCommunityDragonAsync("perks.json", forceRefresh || patchChanged, ct).ConfigureAwait(false);
            if (patchChanged) _client.WriteMarker("cdragon-patch", version);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested)
        {
            _logger.LogWarning("Community Dragon unavailable: {Message}", ex.Message);
            warnings.Add("Stat shards unavailable (Community Dragon could not be reached).");
        }

        progress?.Report("Loading champion positions…");
        var positions = await _client.GetPositionsAsync(forceRefresh, championIds, ct).ConfigureAwait(false);
        if (positions.Count == 0) warnings.Add("Champion positions unavailable; role filters are disabled.");

        var newChampions = await NewChampionsAsync(version, versions, championIds, ct).ConfigureAwait(false);

        try
        {
            var (itemList, shopIds) = GameDataParser.ParseItems(items.RootElement);
            return new GameDataSnapshot(
                version,
                GameDataParser.ParseChampions(champions.RootElement, positions),
                itemList,
                shopIds,
                GameDataParser.ParseRuneTrees(runes.RootElement, perkStyles?.RootElement),
                perkStyles is not null && perks is not null ? GameDataParser.ParseShardRows(perkStyles.RootElement, perks.RootElement) : [],
                GameDataParser.ParseSummonerSpells(spells.RootElement),
                positions.Count > 0,
                newChampions);
        }
        finally
        {
            perkStyles?.Dispose();
            perks?.Dispose();
        }
    }

    private static IReadOnlyCollection<string> ChampionIds(JsonDocument champions) =>
        champions.RootElement.TryGetProperty("data", out var data) ? data.EnumerateObject().Select(p => p.Name).ToList() : [];

    /// <summary>Champions in this patch that weren't in the previous patch (for "NEW" badges).</summary>
    private async Task<IReadOnlyCollection<string>> NewChampionsAsync(string version, IReadOnlyList<string> versions, IReadOnlyCollection<string> current, CancellationToken ct)
    {
        var index = versions.ToList().IndexOf(version);
        var previous = index >= 0 && index + 1 < versions.Count ? versions[index + 1] : null;
        if (previous is null) return [];
        try
        {
            using var doc = await _client.GetDataAsync(previous, "champion.json", ct).ConfigureAwait(false);
            var before = ChampionIds(doc).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return current.Where(id => !before.Contains(id)).ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException && !ct.IsCancellationRequested)
        {
            _logger.LogDebug("Previous patch {Version} unavailable for comparison: {Message}", previous, ex.Message);
            return [];
        }
    }

    /// <summary>Keeps the newest patches' cached data and images; older patches are deleted.</summary>
    private void Prune(IReadOnlyList<string> versions, string current)
    {
        try
        {
            var keep = versions.Take(PatchesKept).Append(current).ToHashSet();
            var data = _client.PruneVersions(keep);
            var images = _assets.PruneVersions(keep);
            if (data.Count > 0 || images > 0)
                _logger.LogInformation("Pruned cached patches: data {Data}; {Images} image folders", string.Join(", ", data), images);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cache pruning failed");
        }
    }

    public async Task<string?> CheckForNewPatchAsync(CancellationToken cancellationToken)
    {
        if (Current is null) return null;
        try
        {
            var (versions, fromCache) = await _client.GetVersionsAsync(cancellationToken).ConfigureAwait(false);
            var newest = versions.FirstOrDefault();
            if (fromCache || newest is null || newest == Current.Version) return null;
            return IsNewer(newest, Current.Version) ? newest : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or JsonException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    internal static bool IsNewer(string candidate, string current) =>
        Version.TryParse(candidate, out var a) && Version.TryParse(current, out var b) ? a > b : !string.Equals(candidate, current, StringComparison.Ordinal);

    public Task<IReadOnlyDictionary<string, ItemInfo>?> GetItemsForVersionAsync(string version, CancellationToken cancellationToken)
    {
        if (Current is { } current && current.Version == version) return Task.FromResult<IReadOnlyDictionary<string, ItemInfo>?>(current.Items);
        return _itemsByVersion.GetOrAdd(version, v => Task.Run(async () =>
        {
            try
            {
                using var doc = await _client.GetDataAsync(v, "item.json", CancellationToken.None).ConfigureAwait(false);
                return (IReadOnlyDictionary<string, ItemInfo>?)GameDataParser.ParseItems(doc.RootElement).Items.ToDictionary(i => i.Id);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException)
            {
                _logger.LogDebug("Items for patch {Version} unavailable: {Message}", v, ex.Message);
                _itemsByVersion.TryRemove(v, out _);
                return null;
            }
        })).WaitAsync(cancellationToken);
    }

    public Task<ChampionAbilities?> GetAbilitiesAsync(string championId, CancellationToken cancellationToken)
    {
        var version = Current?.Version;
        if (version is null) return Task.FromResult<ChampionAbilities?>(null);
        return _abilities.GetOrAdd(championId, id => Task.Run(async () =>
        {
            try
            {
                using var doc = await _client.GetDataAsync(version, $"champion/{id}.json", CancellationToken.None).ConfigureAwait(false);
                return GameDataParser.ParseAbilities(doc.RootElement, id, version);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException)
            {
                _logger.LogWarning("Abilities for {Champion} unavailable: {Message}", id, ex.Message);
                _abilities.TryRemove(id, out _);
                return null;
            }
        })).WaitAsync(cancellationToken);
    }

    private string? NewestCachedVersion() =>
        _client.CachedVersions().OrderByDescending(v => Version.TryParse(v, out var parsed) ? parsed : new Version(0, 0)).FirstOrDefault();
}

public sealed class GameDataUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

public static class IntegrationsServiceCollectionExtensions
{
    public static IServiceCollection AddRosterIntegrations(this IServiceCollection services)
    {
        // Singletons (not typed clients, which are transient): the caches keep in-flight and missing-file state.
        services.AddHttpClient("data", c => c.DefaultRequestHeaders.UserAgent.ParseAdd("RosterControl/1.0"));
        services.AddHttpClient("images", c => c.DefaultRequestHeaders.UserAgent.ParseAdd("RosterControl/1.0"));
        services.AddSingleton(sp => new GameDataClient(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient("data"),
            sp.GetRequiredService<IAppPaths>(),
            sp.GetRequiredService<ILogger<GameDataClient>>()));
        services.AddSingleton<IAssetCache>(sp => new AssetCache(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient("images"),
            sp.GetRequiredService<IAppPaths>(),
            sp.GetRequiredService<ILogger<AssetCache>>()));
        services.AddSingleton<IGameDataService, GameDataService>();
        return services;
    }
}
