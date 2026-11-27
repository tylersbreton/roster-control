using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using RosterControl.Core.Abstractions;
using RosterControl.Core.GameData;
using RosterControl.Core.Model;
using RosterControl.Data;
using RosterControl.Desktop.Services;
using RosterControl.Integrations;

namespace RosterControl.Desktop.ViewModels;

/// <summary>App updates (GitHub releases via Velopack).</summary>
public sealed partial class MainWindowViewModel
{
    // ── App updates (GitHub releases) ──────────────────────────────────────────────────────────────

    private readonly AppUpdateService _updates;
    private DispatcherTimer? _updateTimer;

    public AppUpdateService Updates => _updates;

    /// <summary>A downloaded app update is ready; applied when the user clicks "Restart to update".</summary>
    [ObservableProperty]
    private bool _isAppUpdateReady;

    [ObservableProperty]
    private string _appUpdateText = "";

    private void OnUpdateStateChanged()
    {
        var ready = _updates.State == AppUpdateState.ReadyToRestart;
        if (ready && !IsAppUpdateReady) ShowToast($"Roster Control {_updates.AvailableVersion} is ready — click “Restart to update” when convenient.", ToastKind.Success);
        IsAppUpdateReady = ready;
        AppUpdateText = ready ? $"Version {_updates.AvailableVersion} ready" : "";
        Settings?.RefreshUpdateStatus();
    }

    private void StartUpdateChecks()
    {
        if (!_updates.IsSupported || _updateTimer is not null) return;
        _updateTimer = new DispatcherTimer(AppUpdateService.CheckInterval, DispatcherPriority.Background, (_, _) => _ = _updates.CheckAndDownloadAsync());
        _updateTimer.Start();
        // First check shortly after startup so it doesn't compete with loading game data.
        DispatcherTimer.RunOnce(() => _ = _updates.CheckAndDownloadAsync(), TimeSpan.FromSeconds(10));
    }

    [RelayCommand]
    private async Task RestartToUpdateAsync()
    {
        if (Dialog is not null) return;
        await _saves.FlushAsync();
        await _preferences.FlushAsync(CancellationToken.None);
        _updates.RestartToUpdate();
    }
}
