using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using RosterControl.Core.GameData;

namespace RosterControl.Desktop.ViewModels;

/// <summary>
/// One ability with its numbers: name, description, cooldown/cost/range (Data Dragon) and damage etc. per rank (game
/// files, loaded separately). Used by the Abilities tab, skill order tooltips and the quick-look window.
/// </summary>
public sealed partial class AbilityDetailViewModel(AbilityInfo info) : ObservableObject
{
    public AbilityInfo Info { get; } = info;
    public char Slot => Info.Slot;
    public string Key => Slot == 'P' ? "P" : Slot.ToString();
    public string KeyLabel => Slot == 'P' ? "PASSIVE" : Slot.ToString();
    public string Name => Info.Name;
    public string Description => Info.Description;
    public string IconUrl => Info.ImageUrl;
    public IBrush Accent { get; } = Palette.Hex(info.Slot switch { 'Q' => "#6FA8F5", 'W' => "#6FCB74", 'E' => "#E5C35C", 'R' => "#EE5A6F", _ => "#C8AA6E" });

    public IReadOnlyList<AbilityStat> Stats { get; } = new[]
    {
        new AbilityStat("Cooldown", info.CooldownText),
        new AbilityStat("Cost", info.CostText),
        new AbilityStat("Range", info.RangeText),
    }.Where(s => s.Value.Length > 0).ToList();

    public bool HasStats => Stats.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasValues))]
    private IReadOnlyList<AbilityValue> _values = [];

    public bool HasValues => Values.Count > 0;
}

public sealed record AbilityStat(string Label, string Value);

/// <summary>The champion page's Abilities tab: passive and Q/W/E/R with their numbers.</summary>
public sealed partial class AbilitiesViewModel(IShell shell, string championId) : ObservableObject
{
    private Task? _loading;

    [ObservableProperty]
    private IReadOnlyList<AbilityDetailViewModel> _abilities = [];

    [ObservableProperty]
    private bool _isLoading = true;

    [ObservableProperty]
    private string _note = "";

    [ObservableProperty]
    private bool _isUnavailable;

    public Task EnsureLoadedAsync() => _loading ??= LoadAsync();

    private async Task LoadAsync()
    {
        try
        {
            var abilities = await shell.GameData.GetAbilitiesAsync(championId, CancellationToken.None);
            if (abilities is null)
            {
                IsUnavailable = true;
                Note = "Abilities couldn't be downloaded. Check your connection and open the champion again.";
                return;
            }
            Abilities = abilities.Abilities.Select(a => new AbilityDetailViewModel(a)).ToList();
            IsLoading = false;
            var numbers = await shell.GameData.GetAbilityNumbersAsync(championId, CancellationToken.None);
            Apply(Abilities, numbers);
            Note = numbers is null
                ? "Damage numbers couldn't be downloaded from the game files; cooldowns, costs and ranges are from Data Dragon."
                : $"Numbers from the game files (patch {numbers.Patch}, via Community Dragon). Ratios like +50% AP add that share of the stat; \"by level\" values grow from champion level 1 to 18.";
        }
        finally
        {
            IsLoading = false;
        }
    }

    internal static void Apply(IEnumerable<AbilityDetailViewModel> abilities, AbilityNumbers? numbers)
    {
        if (numbers is null) return;
        foreach (var a in abilities) a.Values = numbers[a.Slot];
    }
}
