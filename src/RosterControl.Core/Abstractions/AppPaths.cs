namespace RosterControl.Core.Abstractions;

public interface IAppPaths
{
    /// <summary>Root of all application data (database, caches, logs, backups).</summary>
    string Root { get; }
    string DatabasePath { get; }
    string CacheDirectory { get; }
    string LogDirectory { get; }
    string BackupDirectory { get; }
    /// <summary>True when data lives next to the executable (portable mode, the default).</summary>
    bool IsPortable { get; }
    StorageMode Mode { get; }
}

public enum StorageMode
{
    /// <summary>data\ next to the executable (default).</summary>
    Portable,
    /// <summary>Folder given with --data (or by tests).</summary>
    Explicit,
    /// <summary>%LOCALAPPDATA%\RosterControl because the application folder is not writable.</summary>
    UserProfileFallback,
}

/// <summary>
/// Data is local to each deployed copy of the application: it lives in a <c>data</c> folder next to the executable,
/// so every unzipped/deployed folder has its own rosters. If that folder is not writable (for example when installed
/// under Program Files) the per-user folder %LOCALAPPDATA%\RosterControl is used instead.
/// </summary>
public sealed class AppPaths : IAppPaths
{
    public const string ApplicationFolderName = "RosterControl";

    public AppPaths() : this(null) { }

    /// <param name="rootOverride">Explicit data folder (tests, or the <c>--data</c> command line switch).</param>
    public AppPaths(string? rootOverride)
    {
        if (rootOverride is not null)
        {
            Root = Path.GetFullPath(rootOverride);
            Mode = StorageMode.Explicit;
        }
        else
        {
            var portable = Path.Combine(PortableRoot(AppContext.BaseDirectory), "data");
            if (IsWritable(portable))
            {
                Root = portable;
                Mode = StorageMode.Portable;
            }
            else
            {
                Root = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create),
                    ApplicationFolderName);
                Mode = StorageMode.UserProfileFallback;
            }
        }
        CacheDirectory = Path.Combine(Root, "cache");
        LogDirectory = Path.Combine(Root, "logs");
        BackupDirectory = Path.Combine(Root, "backups");
        DatabasePath = Path.Combine(Root, "roster.db");
        foreach (var dir in new[] { Root, CacheDirectory, LogDirectory, BackupDirectory })
            Directory.CreateDirectory(dir);
    }

    public string Root { get; }
    public string DatabasePath { get; }
    public string CacheDirectory { get; }
    public string LogDirectory { get; }
    public string BackupDirectory { get; }
    public bool IsPortable => Mode == StorageMode.Portable;
    public StorageMode Mode { get; }

    /// <summary>
    /// The folder the user unzipped. Self-updating releases (Velopack) run from <c>&lt;root&gt;\current\</c>, which is
    /// replaced on every update, so data must live in <c>&lt;root&gt;\data</c>, never inside <c>current</c>.
    /// </summary>
    public static string PortableRoot(string baseDirectory)
    {
        var dir = new DirectoryInfo(baseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.Equals(dir.Name, "current", StringComparison.OrdinalIgnoreCase) && dir.Parent is { } root
            && File.Exists(Path.Combine(root.FullName, "Update.exe")))
            return root.FullName;
        return dir.FullName;
    }

    private static bool IsWritable(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, ".write-test-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
