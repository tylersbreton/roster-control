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

/// <summary>The quick-look window and its global shortcut.</summary>
public sealed partial class MainWindowViewModel
{
    // ── Quick look (always-on-top build window) and its global shortcut ────────────────────────────

    private const string QuickLookHotkeyKey = "quicklook.hotkey";
    private const string QuickLookHotkeyEnabledKey = "quicklook.hotkeyEnabled";
    public const string DefaultQuickLookHotkey = "Ctrl+Shift+Space";

    private QuickLookViewModel? _quickLook;
    private Func<string?, bool>? _registerHotkey;

    public QuickLookViewModel QuickLook => _quickLook ??= new QuickLookViewModel(this) { ShortcutHint = QuickLookShortcutHint };

    /// <param name="Toggle">From the global shortcut: hides the window when it's already in front.</param>
    public sealed record QuickLookRequest(bool Toggle, bool FocusSearch);

    /// <summary>Raised to show (or toggle) the quick-look window; the main window owns it.</summary>
    public event EventHandler<QuickLookRequest>? QuickLookRequested;

    public void OpenQuickLook(string? championId = null, Position? role = null, int? buildId = null)
    {
        QuickLookRequested?.Invoke(this, new QuickLookRequest(false, championId is null));
        _ = championId is null ? QuickLook.RestoreLastAsync() : QuickLook.ShowChampionAsync(championId, role, buildId);
    }

    /// <summary>The global shortcut was pressed.</summary>
    public void ToggleQuickLook()
    {
        QuickLookRequested?.Invoke(this, new QuickLookRequest(true, true));
        _ = QuickLook.RestoreLastAsync();
    }

    public void BringToFront()
    {
        if (_topLevel?.Invoke() is not Window window) return;
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }

    /// <summary>The global shortcut is on (default) and registered with Windows.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(QuickLookShortcutHint))]
    private bool _quickLookHotkeyEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(QuickLookShortcutHint), nameof(HotkeyButtonText))]
    private string _quickLookHotkey = DefaultQuickLookHotkey;

    /// <summary>Waiting for a new shortcut to be pressed (Settings); the current one is released meanwhile.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HotkeyButtonText))]
    private bool _isRecordingHotkey;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHotkeyStatus))]
    private string _hotkeyStatus = "";

    public bool HasHotkeyStatus => HotkeyStatus.Length > 0;
    public string HotkeyButtonText => IsRecordingHotkey ? "Press a shortcut… (Esc cancels)" : QuickLookHotkey;
    public string QuickLookShortcutHint => QuickLookHotkeyEnabled ? $"{QuickLookHotkey} shows or hides this window from anywhere" : "";

    partial void OnQuickLookHotkeyEnabledChanged(bool value)
    {
        _preferences.Set(QuickLookHotkeyEnabledKey, value);
        ApplyHotkey();
    }

    partial void OnQuickLookHotkeyChanged(string value)
    {
        _preferences.Set(QuickLookHotkeyKey, value);
        ApplyHotkey();
    }

    partial void OnIsRecordingHotkeyChanged(bool value) => ApplyHotkey();

    /// <summary>Connects the shortcut to Windows (null unregisters); returns false when another app already uses it.</summary>
    public void AttachHotkey(Func<string?, bool> register)
    {
        _registerHotkey = register;
        ApplyHotkey();
    }

    private void ApplyHotkey()
    {
        if (_quickLook is not null) _quickLook.ShortcutHint = QuickLookShortcutHint;
        if (_registerHotkey is null) return;
        if (!QuickLookHotkeyEnabled || IsRecordingHotkey)
        {
            _registerHotkey(null);
            if (!IsRecordingHotkey) HotkeyStatus = "";
            return;
        }
        var ok = _registerHotkey(QuickLookHotkey);
        HotkeyStatus = ok ? "" : $"{QuickLookHotkey} is already used by another app. Choose a different shortcut.";
        if (!ok) _logger.LogWarning("Could not register the quick look shortcut {Shortcut}", QuickLookHotkey);
    }

    [RelayCommand]
    private void RecordHotkey()
    {
        HotkeyStatus = "";
        IsRecordingHotkey = true;
    }

    [RelayCommand]
    private void ResetHotkey()
    {
        IsRecordingHotkey = false;
        QuickLookHotkey = DefaultQuickLookHotkey;
        ApplyHotkey();
    }

    /// <summary>A key pressed while recording a new shortcut (Settings). Always consumes the key.</summary>
    public void RecordHotkeyKey(Key key, KeyModifiers modifiers)
    {
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or Key.System)
            return; // wait for the actual key
        if (key == Key.Escape)
        {
            IsRecordingHotkey = false;
            return;
        }
        var gesture = new KeyGesture(key, modifiers);
        if (!GlobalHotkey.IsValid(gesture))
        {
            HotkeyStatus = "Use Ctrl, Alt or Win together with a letter, number, F-key or Space.";
            return;
        }
        HotkeyStatus = "";
        var text = GlobalHotkey.Format(gesture);
        IsRecordingHotkey = false;
        if (text == QuickLookHotkey) ApplyHotkey();
        else QuickLookHotkey = text;
    }
}
