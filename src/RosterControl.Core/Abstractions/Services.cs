using RosterControl.Core.GameData;

namespace RosterControl.Core.Abstractions;

/// <summary>Downloads public images once and serves them from the local disk cache afterwards.</summary>
public interface IAssetCache
{
    string? TryGetCachedPath(string url);
    Task<string?> GetAsync(string url, CancellationToken cancellationToken);
    /// <summary>Deletes cached images of patches not listed. Returns the number of patches removed.</summary>
    int PruneVersions(IReadOnlyCollection<string> keep);
}

public enum GameDataSource
{
    /// <summary>Freshly checked against Data Dragon.</summary>
    Online,
    /// <summary>Loaded from the local cache because the network was unavailable.</summary>
    Cache,
    /// <summary>Loaded from the cache for a fast start; the patch list is checked online in the background.</summary>
    StartupCache,
}

public sealed record GameDataStatus(string Version, GameDataSource Source, DateTimeOffset LoadedAt, IReadOnlyList<string> Warnings);

/// <summary>Reference data (champions, items, runes, spells) for the newest available Data Dragon version.</summary>
public interface IGameDataService
{
    GameDataSnapshot? Current { get; }
    GameDataStatus? Status { get; }
    /// <summary>When the patch list was last fetched from Data Dragon successfully.</summary>
    DateTimeOffset? LastPatchCheck { get; }

    /// <summary>Loads the newest fully cached patch without network access (fast start). Null when nothing is cached.</summary>
    Task<GameDataSnapshot?> LoadCachedAsync(IProgress<string>? progress, CancellationToken cancellationToken);
    /// <summary>
    /// Loads the newest patch (checking the patch list online). <paramref name="forceRefresh"/> also re-downloads the
    /// non-versioned documents (stat shards, champion positions) instead of using their cached copies.
    /// </summary>
    Task<GameDataSnapshot> LoadAsync(bool forceRefresh, IProgress<string>? progress, CancellationToken cancellationToken);
    Task<ChampionAbilities?> GetAbilitiesAsync(string championId, CancellationToken cancellationToken);

    /// <summary>Damage, ratios and other ability numbers from the game files (Community Dragon). Null if unavailable.</summary>
    Task<AbilityNumbers?> GetAbilityNumbersAsync(string championId, CancellationToken cancellationToken);

    /// <summary>Formulas of runes such as Electrocute or Grasp (game files, Community Dragon). Null if unavailable.</summary>
    Task<RuneNumbers?> GetRuneNumbersAsync(CancellationToken cancellationToken);

    /// <summary>Checks Data Dragon for a patch newer than <see cref="Current"/>. Returns it, or null (also when offline).</summary>
    Task<string?> CheckForNewPatchAsync(CancellationToken cancellationToken);

    /// <summary>Item data of another patch (to show what changed since a build was saved). Null if unavailable.</summary>
    Task<IReadOnlyDictionary<string, ItemInfo>?> GetItemsForVersionAsync(string version, CancellationToken cancellationToken);
}
