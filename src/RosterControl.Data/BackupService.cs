using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using RosterControl.Core.Abstractions;

namespace RosterControl.Data;

/// <summary>JSON export/import of all user data, plus rolling automatic backups in <c>data\backups</c>.</summary>
public sealed class BackupService
{
    public const int AutomaticBackupsKept = 10;

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly RosterRepository _repository;
    private readonly IAppPaths _paths;
    private readonly ILogger<BackupService> _logger;

    public BackupService(RosterRepository repository, IAppPaths paths, ILogger<BackupService> logger)
    {
        _repository = repository;
        _paths = paths;
        _logger = logger;
    }

    public string BackupDirectory => _paths.BackupDirectory;

    public async Task ExportAsync(Stream destination, string? gameVersion, CancellationToken ct = default)
    {
        var backup = await _repository.ExportAsync(gameVersion, ct).ConfigureAwait(false);
        await JsonSerializer.SerializeAsync(destination, backup, Json, ct).ConfigureAwait(false);
    }

    public async Task ExportToFileAsync(string path, string? gameVersion, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var tmp = path + ".tmp";
        await using (var fs = File.Create(tmp))
            await ExportAsync(fs, gameVersion, ct).ConfigureAwait(false);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Validates and reads a backup without changing anything.</summary>
    public static async Task<RosterBackup> ReadAsync(Stream source, CancellationToken ct = default)
    {
        RosterBackup? backup;
        try
        {
            backup = await JsonSerializer.DeserializeAsync<RosterBackup>(source, Json, ct).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The file is not a Roster Control backup (invalid JSON).", ex);
        }
        if (backup is null || backup.SchemaVersion < 1)
            throw new InvalidDataException("The file is not a Roster Control backup.");
        if (backup.SchemaVersion > RosterBackup.CurrentSchemaVersion)
            throw new InvalidDataException("This backup was made by a newer version of Roster Control.");
        return backup;
    }

    /// <summary>Replaces all data with the backup. A safety backup of the current data is written first.</summary>
    public async Task ImportAsync(RosterBackup backup, string? gameVersion, CancellationToken ct = default)
    {
        var safety = Path.Combine(_paths.BackupDirectory, $"before-import-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        await ExportToFileAsync(safety, gameVersion, ct).ConfigureAwait(false);
        _logger.LogInformation("Safety backup written to {Path} before import", safety);
        await _repository.ImportAsync(backup, ct).ConfigureAwait(false);
    }

    /// <summary>Writes an automatic backup at most once per day and keeps the newest <see cref="AutomaticBackupsKept"/>.</summary>
    public async Task<string?> RunAutomaticBackupAsync(string? gameVersion, CancellationToken ct = default)
    {
        try
        {
            var existing = Directory.EnumerateFiles(_paths.BackupDirectory, "auto-*.json").OrderDescending().ToList();
            if (existing.Count > 0 && DateTime.Now - File.GetLastWriteTime(existing[0]) < TimeSpan.FromHours(20)) return null;
            var path = Path.Combine(_paths.BackupDirectory, $"auto-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            await ExportToFileAsync(path, gameVersion, ct).ConfigureAwait(false);
            foreach (var old in Directory.EnumerateFiles(_paths.BackupDirectory, "auto-*.json").OrderDescending().Skip(AutomaticBackupsKept))
                File.Delete(old);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Automatic backup failed");
            return null;
        }
    }
}
