using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RosterControl.Desktop.Services;

namespace RosterControl.Desktop.ViewModels;

/// <summary>Light/dark theme and accent colour (Settings → Display). Applied instantly; stored as preferences.</summary>
public sealed partial class MainWindowViewModel
{
    public const string ThemeKey = "ui.theme";
    public const string AccentKey = "ui.accent";

    private bool _themeLoaded;

    /// <summary>Dark (default), Light, or follow Windows.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDarkTheme), nameof(IsLightTheme), nameof(IsSystemTheme))]
    private ThemeMode _theme;

    /// <summary>Accent colour as #RRGGBB (gold by default).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CustomAccentColor), nameof(AccentText))]
    private string _accent = AppTheme.DefaultAccent;

    public bool IsDarkTheme { get => Theme == ThemeMode.Dark; set { if (value) Theme = ThemeMode.Dark; } }
    public bool IsLightTheme { get => Theme == ThemeMode.Light; set { if (value) Theme = ThemeMode.Light; } }
    public bool IsSystemTheme { get => Theme == ThemeMode.System; set { if (value) Theme = ThemeMode.System; } }

    public IReadOnlyList<AccentChoice> AccentChoices { get; } = AppTheme.Presets.Select(p => new AccentChoice(p.Name, p.Hex)).ToList();

    public string AccentText => AccentChoices.FirstOrDefault(c => c.Hex == Accent)?.Name ?? $"Custom {Accent}";

    /// <summary>For the colour picker (any colour).</summary>
    public Color CustomAccentColor
    {
        get => Color.Parse(Accent);
        set => Accent = AppTheme.Normalize($"#{value.R:X2}{value.G:X2}{value.B:X2}");
    }

    private void LoadTheme()
    {
        // Applied by App at startup; here only the settings shown in Settings.
        var theme = _preferences.Get(ThemeKey, ThemeMode.Dark);
        Theme = Enum.IsDefined(theme) ? theme : ThemeMode.Dark;
        Accent = AppTheme.Normalize(_preferences.Get(AccentKey, AppTheme.DefaultAccent));
        foreach (var c in AccentChoices) c.IsSelected = c.Hex == Accent;
        _themeLoaded = true;
    }

    partial void OnThemeChanged(ThemeMode value)
    {
        if (!_themeLoaded) return;
        _preferences.Set(ThemeKey, value);
        ApplyTheme();
    }

    partial void OnAccentChanged(string value)
    {
        if (!_themeLoaded) return;
        var normalized = AppTheme.Normalize(value);
        if (normalized != value)
        {
            Accent = normalized;
            return;
        }
        _preferences.Set(AccentKey, value);
        foreach (var c in AccentChoices) c.IsSelected = c.Hex == value;
        ApplyTheme();
    }

    [RelayCommand]
    private void SetAccent(string hex) => Accent = AppTheme.Normalize(hex);

    [RelayCommand]
    private void SetTheme(ThemeMode mode) => Theme = mode;

    private void ApplyTheme()
    {
        if (Application.Current is { } app) AppTheme.Apply(app, Theme, Accent);
    }
}

/// <summary>An accent preset in Settings.</summary>
public sealed partial class AccentChoice(string name, string hex) : ObservableObject
{
    public string Name { get; } = name;
    public string Hex { get; } = hex;
    public IBrush Brush { get; } = new SolidColorBrush(Color.Parse(hex)).ToImmutable();

    [ObservableProperty]
    private bool _isSelected;
}
