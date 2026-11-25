using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RosterControl.Data;
using RosterControl.Desktop.Controls;
using RosterControl.Desktop.Services;
using RosterControl.Desktop.ViewModels;
using RosterControl.Desktop.Views;

namespace RosterControl.Desktop;

public sealed class App : Application
{
    public static IHost? Host { get; set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        var services = Host?.Services ?? throw new InvalidOperationException("Host not built.");
        InitializeServices(services);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vm = services.GetRequiredService<MainWindowViewModel>();
            var window = new MainWindow(services.GetRequiredService<PreferencesStore>()) { DataContext = vm };
            desktop.MainWindow = window;
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            desktop.Exit += (_, _) =>
            {
                services.GetRequiredService<SaveCoordinator>().FlushBlocking();
                services.GetRequiredService<PreferencesStore>().FlushAsync(CancellationToken.None).GetAwaiter().GetResult();
            };
            _ = vm.InitializeAsync();
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Shared initialisation (also used by the headless UI tests).</summary>
    public static void InitializeServices(IServiceProvider services)
    {
        services.GetRequiredService<PreferencesStore>().Load();
        AssetImage.Images = services.GetRequiredService<AssetImageService>();
    }
}
