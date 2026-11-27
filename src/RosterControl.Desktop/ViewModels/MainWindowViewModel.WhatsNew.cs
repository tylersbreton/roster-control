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

/// <summary>What's new after an update (Settings → About too).</summary>
public sealed partial class MainWindowViewModel
{
    // ── What's new ─────────────────────────────────────────────────────────────────────────────────

    [ObservableProperty]
    private WhatsNewViewModel? _whatsNew;

    private static IReadOnlyList<Core.Rules.ChangelogEntry> LoadChangelog()
    {
        using var stream = typeof(MainWindowViewModel).Assembly.GetManifestResourceStream("CHANGELOG.md");
        if (stream is null) return [];
        using var reader = new StreamReader(stream);
        return Core.Rules.Changelog.Parse(reader.ReadToEnd());
    }

    /// <summary>After an update, shows what changed since the version last run (nothing on the first run).</summary>
    private void ShowWhatsNewAfterUpdate()
    {
        if (!Core.Rules.Changelog.TryParseVersion(_updates.CurrentVersion, out var current)) return;
        // Versions before 1.3 didn't record this: an existing install without it is coming from 1.2.
        var lastSeenText = _preferences.Get<string?>(LastSeenVersionKey, null) ?? (_returningUser ? "1.2.0" : null);
        _preferences.Set(LastSeenVersionKey, current.ToString(3));
        if (!Core.Rules.Changelog.TryParseVersion(lastSeenText, out var lastSeen) || lastSeen >= current) return;
        var entries = Core.Rules.Changelog.Between(LoadChangelog(), lastSeen, current);
        if (entries.Count > 0) WhatsNew = new WhatsNewViewModel(this, $"What's new in Roster Control {current.ToString(3)}", entries);
    }

    [RelayCommand]
    public void ShowWhatsNew()
    {
        var entries = LoadChangelog().Take(3).ToList();
        if (entries.Count > 0) WhatsNew = new WhatsNewViewModel(this, "What's new", entries);
    }
}

/// <summary>The "What's new" dialog (after an update, or from Settings → About).</summary>
public sealed partial class WhatsNewViewModel(MainWindowViewModel owner, string title, IReadOnlyList<Core.Rules.ChangelogEntry> entries) : ObservableObject
{
    public string Title { get; } = title;
    public IReadOnlyList<WhatsNewEntry> Entries { get; } = entries.Select(e => new WhatsNewEntry(e.Version.ToString(3), e.Body)).ToList();

    [RelayCommand]
    private void Close() => owner.WhatsNew = null;
}

public sealed record WhatsNewEntry(string Version, string Body)
{
    public string Heading => "Version " + Version;
}
