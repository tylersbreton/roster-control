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

/// <summary>Clipboard, confirmation dialogs, toasts and file pickers.</summary>
public sealed partial class MainWindowViewModel
{
    // ── Dialogs & toasts ───────────────────────────────────────────────────────────────────────────

    public async Task<bool> CopyTextAsync(string text)
    {
        if (_topLevel?.Invoke()?.Clipboard is not { } clipboard) return false;
        try
        {
            await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(clipboard, text);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Clipboard write failed");
            return false;
        }
    }

    private async Task<string?> ReadTextAsync()
    {
        if (_topLevel?.Invoke()?.Clipboard is not { } clipboard) return null;
        try
        {
            return await Avalonia.Input.Platform.ClipboardExtensions.TryGetTextAsync(clipboard);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Clipboard read failed");
            return null;
        }
    }

    public async Task<Core.Model.Build?> ImportSharedBuildAsync(string? targetChampionId)
    {
        var text = await ReadTextAsync();
        if (!Core.Rules.BuildShareCode.TryDecode(text, out var build, out var error))
        {
            ShowToast(string.IsNullOrWhiteSpace(text) ? "Copy a build code (it starts with RC1:) first, then paste it here." : error!, ToastKind.Warning);
            return null;
        }
        if (_game?.Champion(build.ChampionId) is not { } champion)
        {
            ShowToast($"That build is for a champion that isn't in patch {_game?.Version} yet.", ToastKind.Warning);
            return null;
        }
        build.ChampionId = champion.Id;
        if (targetChampionId is not null && !string.Equals(targetChampionId, champion.Id, StringComparison.OrdinalIgnoreCase))
        {
            var here = _game.Champion(targetChampionId)?.Name ?? targetChampionId;
            if (!await ConfirmAsync($"Add to {champion.Name}?", $"This code is a {champion.Name} build, not a {here} build. It will be added to {champion.Name}'s builds.", $"Add to {champion.Name}", danger: false))
                return null;
        }
        build.GameVersion ??= _game.Version;
        build.Id = await _repository.SaveBuildAsync(build);
        MarkDirty();
        return build;
    }

    public async Task<bool> ConfirmAsync(string title, string message, string confirmText, bool danger = true)
    {
        var dialog = new ConfirmDialogViewModel(title, message, confirmText, danger);
        Dialog = dialog;
        try
        {
            return await dialog.Result;
        }
        finally
        {
            if (ReferenceEquals(Dialog, dialog)) Dialog = null;
        }
    }

    public void ShowToast(string message, ToastKind kind = ToastKind.Info, string? actionText = null, Func<Task>? action = null)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var toast = new ToastViewModel(message, kind, actionText, action);
            Toasts.Add(toast);
            while (Toasts.Count > 3) Toasts.RemoveAt(0);
            var seconds = action is not null ? 8 : kind is ToastKind.Error or ToastKind.Warning ? 6 : 3.2;
            DispatcherTimer.RunOnce(() => Toasts.Remove(toast), TimeSpan.FromSeconds(seconds));
        });
    }

    [RelayCommand]
    private void DismissToast(ToastViewModel toast) => Toasts.Remove(toast);

    [RelayCommand]
    private async Task RunToastActionAsync(ToastViewModel toast)
    {
        Toasts.Remove(toast);
        if (toast.Action is null) return;
        try
        {
            await toast.Action();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Toast action failed");
            ShowToast("That couldn't be undone: " + ex.Message, ToastKind.Error);
        }
    }

    /// <summary>Ctrl+Z: runs the newest Undo toast (other toast actions, such as Open, are left alone).</summary>
    public bool UndoLast()
    {
        var toast = Toasts.LastOrDefault(t => t.HasAction && t.ActionText == "Undo");
        if (toast is null) return false;
        RunToastActionCommand.Execute(toast);
        return true;
    }

    public async Task<string?> PickSaveFileAsync(string title, string suggestedName)
    {
        if (_topLevel?.Invoke()?.StorageProvider is not { } storage) return null;
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = "json",
            FileTypeChoices = [new FilePickerFileType("Roster Control backup") { Patterns = ["*.json"] }],
            SuggestedStartLocation = await storage.TryGetFolderFromPathAsync(_paths.BackupDirectory),
        });
        return file?.TryGetLocalPath();
    }

    public async Task<string?> PickOpenFileAsync(string title)
    {
        if (_topLevel?.Invoke()?.StorageProvider is not { } storage) return null;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Roster Control backup") { Patterns = ["*.json"] }],
            SuggestedStartLocation = await storage.TryGetFolderFromPathAsync(_paths.BackupDirectory),
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }
}
