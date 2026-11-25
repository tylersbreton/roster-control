using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RosterControl.Core.Abstractions;
using RosterControl.Data;
using RosterControl.Desktop.Services;
using RosterControl.Desktop.ViewModels;
using RosterControl.Integrations;
using Serilog;

namespace RosterControl.Desktop;

public static class AppHost
{
    public static IHost Build(IAppPaths paths, Action<IServiceCollection>? configure = null)
    {
        // DisableDefaults: no appsettings.json or environment config files — zero configuration.
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true, ApplicationName = "RosterControl" });
        builder.Services.AddSerilog();
        builder.Services.AddSingleton(paths);
        builder.Services.AddRosterIntegrations();
        builder.Services.AddRosterData();
        builder.Services.AddSingleton<AssetImageService>();
        builder.Services.AddSingleton<SaveCoordinator>();
        builder.Services.AddSingleton<AppUpdateService>();
        builder.Services.AddSingleton<MainWindowViewModel>();
        configure?.Invoke(builder.Services);
        return builder.Build();
    }
}
