using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RosterControl.Core.GameData;
using RosterControl.Core.Model;
using RosterControl.Desktop.Controls;

namespace RosterControl.Desktop.ViewModels;

/// <summary>Several rosters (Solo/Duo, Flex, ARAM…): switching, creating, renaming, sharing as a file.</summary>
public sealed partial class RostersViewModel
{
    // ── Rosters (Solo/Duo, Flex, ARAM…) ────────────────────────────────────────────────────────────

    public ObservableCollection<RosterChoice> Rosters { get; } = [];
    public int ActiveRosterId { get; private set; }

    [ObservableProperty]
    private string _rosterName = "";

    [ObservableProperty]
    private string _newRosterName = "";

    [ObservableProperty]
    private string _renameText = "";

    public bool CanDeleteRoster => Rosters.Count > 1;
    public string RosterHeading => RosterName.ToUpperInvariant();

    partial void OnRosterNameChanged(string value) => OnPropertyChanged(nameof(RosterHeading));

    private async Task LoadRostersAsync()
    {
        var repo = _shell.Repository;
        var rosters = await repo.GetRostersAsync();
        ActiveRosterId = await repo.GetActiveRosterIdAsync();
        Rosters.Clear();
        foreach (var r in rosters)
        {
            var champions = (await repo.GetGroupsAsync(r.Id)).SelectMany(g => g.Entries).Select(e => e.ChampionId)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count();
            Rosters.Add(new RosterChoice(r.Id, r.Name, champions, r.Id == ActiveRosterId));
        }
        RosterName = Rosters.FirstOrDefault(r => r.IsActive)?.Name ?? "";
        RenameText = RosterName;
        OnPropertyChanged(nameof(CanDeleteRoster));
    }

    [RelayCommand]
    private Task SwitchRosterAsync(RosterChoice roster) => roster.IsActive ? Task.CompletedTask : _shell.SwitchRosterAsync(roster.Id);

    /// <param name="template">"Roles", "Empty" or "Copy".</param>
    [RelayCommand]
    private async Task NewRosterAsync(string template)
    {
        var kind = Enum.TryParse<RosterTemplate>(template, out var t) ? t : RosterTemplate.Roles;
        var name = (NewRosterName ?? "").Trim();
        if (name.Length == 0) name = kind == RosterTemplate.Copy ? $"{RosterName} (copy)" : "New roster";
        for (var n = 2; Rosters.Any(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)); n++)
            name = $"{name.TrimEnd(' ', '0', '1', '2', '3', '4', '5', '6', '7', '8', '9')} {n}";
        var roster = await _shell.Repository.AddRosterAsync(name, kind, ActiveRosterId);
        NewRosterName = "";
        await _shell.SwitchRosterAsync(roster.Id);
        _shell.ShowToast($"Created roster \"{roster.Name}\"", ToastKind.Success);
    }

    [RelayCommand]
    private async Task RenameRosterAsync()
    {
        var name = (RenameText ?? "").Trim();
        if (name.Length == 0 || name == RosterName) return;
        await _shell.Repository.RenameRosterAsync(ActiveRosterId, name);
        await LoadRostersAsync();
    }

    // ── Sharing a roster as a file ─────────────────────────────────────────────────────────────────

    /// <summary>The share (export) or add (import) dialog; null when closed.</summary>
    [ObservableProperty]
    private RosterShareViewModel? _shareDialog;

    [RelayCommand]
    private async Task ShareRosterAsync() => ShareDialog = await RosterShareViewModel.ForExportAsync(this, _shell);

    [RelayCommand]
    private async Task ImportRosterAsync()
    {
        var path = await _shell.PickOpenFileAsync("Add a shared roster", Core.Rules.SharedRosterFile.Extension, "Roster Control roster");
        if (path is not null) await OpenSharedRosterFileAsync(path);
    }

    /// <summary>Reads a shared roster file and shows what it contains before anything is added.</summary>
    public async Task OpenSharedRosterFileAsync(string path)
    {
        string text;
        try
        {
            if (new FileInfo(path).Length > Core.Rules.SharedRosterFile.MaxFileBytes)
            {
                _shell.ShowToast("That file is too large to be a roster file.", ToastKind.Warning);
                return;
            }
            text = await File.ReadAllTextAsync(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _shell.ShowToast("Couldn't read the file: " + ex.Message, ToastKind.Error);
            return;
        }
        if (!Core.Rules.SharedRosterFile.TryParse(text, out var file, out var error))
        {
            _shell.ShowToast(error!, ToastKind.Warning);
            return;
        }
        ShareDialog = RosterShareViewModel.ForImport(this, _shell, file);
    }

    [RelayCommand]
    private async Task DeleteRosterAsync()
    {
        if (!CanDeleteRoster) return;
        var count = Groups.Sum(g => g.Entries.Count);
        var ok = await _shell.ConfirmAsync($"Delete the roster \"{RosterName}\"?",
            $"Its {Groups.Count} group{(Groups.Count == 1 ? "" : "s")} and {count} placement{(count == 1 ? "" : "s")} are removed. Builds, matchups and notes are shared by all rosters and are kept.",
            "Delete roster");
        if (!ok) return;
        var name = RosterName;
        await _shell.Repository.DeleteRosterAsync(ActiveRosterId);
        var next = Rosters.First(r => r.Id != ActiveRosterId);
        await _shell.SwitchRosterAsync(next.Id);
        _shell.ShowToast($"Deleted roster \"{name}\"");
    }
}
