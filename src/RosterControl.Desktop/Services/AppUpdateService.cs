using System.Reflection;
using Microsoft.Extensions.Logging;
using Velopack;
using Velopack.Sources;

namespace RosterControl.Desktop.Services;

public enum AppUpdateState
{
    /// <summary>Running from a development build (not a release): updates are unavailable.</summary>
    NotSupported,
    Idle,
    Checking,
    Downloading,
    /// <summary>An update has been downloaded and is applied by restarting.</summary>
    ReadyToRestart,
    UpToDate,
    Failed,
}

/// <summary>
/// Self-update from GitHub Releases (Velopack). Updates are checked at startup and every few hours, downloaded in the
/// background, and applied only when the user chooses "Restart to update" — never in the middle of editing.
/// </summary>
public sealed class AppUpdateService
{
    public const string RepositoryUrl = "https://github.com/tylersbreton/roster-control";
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(4);

    private readonly ILogger<AppUpdateService> _logger;
    private readonly UpdateManager? _manager;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private UpdateInfo? _ready;

    public AppUpdateService(ILogger<AppUpdateService> logger)
    {
        _logger = logger;
        try
        {
            var manager = new UpdateManager(new GithubSource(RepositoryUrl, accessToken: null, prerelease: false));
            if (manager.IsInstalled) _manager = manager;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Update manager unavailable");
        }
        State = _manager is null ? AppUpdateState.NotSupported : AppUpdateState.Idle;
    }

    public event EventHandler? Changed;

    public AppUpdateState State { get; private set; }
    public bool IsSupported => _manager is not null;
    public string? AvailableVersion { get; private set; }
    public int DownloadProgress { get; private set; }
    public DateTimeOffset? LastChecked { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>The running version (from the release package, or the assembly in development builds).</summary>
    public string CurrentVersion =>
        _manager?.CurrentVersion?.ToString()
        ?? Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
        ?? "dev";

    /// <summary>Checks GitHub and, if a newer release exists, downloads it. Safe to call repeatedly.</summary>
    public async Task CheckAndDownloadAsync(CancellationToken ct = default)
    {
        if (_manager is null || State is AppUpdateState.ReadyToRestart) return;
        if (!await _gate.WaitAsync(0, ct)) return;
        try
        {
            // An update downloaded in an earlier session (but not applied) can be applied right away.
            if (_manager.UpdatePendingRestart is { } pending)
            {
                AvailableVersion = pending.Version.ToString();
                SetState(AppUpdateState.ReadyToRestart);
                return;
            }

            SetState(AppUpdateState.Checking);
            var info = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
            LastChecked = DateTimeOffset.Now;
            if (info is null)
            {
                SetState(AppUpdateState.UpToDate);
                return;
            }

            AvailableVersion = info.TargetFullRelease.Version.ToString();
            _logger.LogInformation("Downloading update {Version}", AvailableVersion);
            DownloadProgress = 0;
            SetState(AppUpdateState.Downloading);
            await _manager.DownloadUpdatesAsync(info, p =>
            {
                DownloadProgress = p;
                Changed?.Invoke(this, EventArgs.Empty);
            }, ct).ConfigureAwait(false);
            _ready = info;
            _logger.LogInformation("Update {Version} ready", AvailableVersion);
            SetState(AppUpdateState.ReadyToRestart);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Offline, GitHub rate limit, etc.: try again at the next check.
            _logger.LogWarning("Update check failed: {Message}", ex.Message);
            LastError = Integrations.NetworkErrors.Describe(ex, "GitHub");
            LastChecked = DateTimeOffset.Now;
            SetState(AppUpdateState.Failed);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Exits, applies the downloaded update and starts the new version.</summary>
    public void RestartToUpdate()
    {
        if (_manager is null) return;
        var asset = _ready?.TargetFullRelease ?? _manager.UpdatePendingRestart;
        if (asset is null) return;
        _logger.LogInformation("Restarting to apply update {Version}", asset.Version);
        _manager.ApplyUpdatesAndRestart(asset);
    }

    private void SetState(AppUpdateState state)
    {
        State = state;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
