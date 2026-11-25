using Avalonia;
using Microsoft.Extensions.Hosting;
using RosterControl.Core.Abstractions;
using Serilog;
using Serilog.Events;

namespace RosterControl.Desktop;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Must run first: handles Velopack's install/update hooks (and exits for them) before any UI starts.
        Velopack.VelopackApp.Build().Run();

        var paths = new AppPaths(DataOverride(args));
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("System.Net.Http", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.File(Path.Combine(paths.LogDirectory, "roster-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 10,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .WriteTo.Debug()
            .CreateLogger();

        IHost? host = null;
        try
        {
            Log.Information("Roster Control starting; data folder {Root} (portable: {Portable})", paths.Root, paths.IsPortable);
            host = AppHost.Build(paths);
            App.Host = host;
            host.Start();
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Fatal startup error");
            return 1;
        }
        finally
        {
            try
            {
                host?.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                host?.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Shutdown error");
            }
            Log.CloseAndFlush();
        }
    }

    /// <summary><c>--data &lt;folder&gt;</c> stores data somewhere other than next to the executable.</summary>
    private static string? DataOverride(string[] args)
    {
        var i = Array.IndexOf(args, "--data");
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();
}
