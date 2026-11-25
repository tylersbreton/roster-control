using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using Microsoft.Extensions.Hosting;
using RosterControl.Core.Abstractions;
using RosterControl.Desktop;

[assembly: Avalonia.Headless.AvaloniaTestApplication(typeof(RosterControl.UITests.UiTestHost))]

namespace RosterControl.UITests;

/// <summary>
/// Boots the real application (same App, styles, view models and services) on Avalonia's headless platform with Skia
/// rendering so frames can be captured. Uses a throw-away data folder; the image/data cache is kept between runs.
/// </summary>
public static class UiTestHost
{
    private static IHost? _host;

    public static string Root { get; } = Path.Combine(Path.GetTempPath(), "rostercontrol-uitests");
    public static IServiceProvider Services => _host!.Services;

    public static string ScreenshotDirectory
    {
        get
        {
            var dir = Path.Combine(FindRepoRoot(), "artifacts", "screenshots");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        // Fresh database per run; downloaded game data and images are reused.
        foreach (var f in new[] { "roster.db", "roster.db-journal" })
        {
            var path = Path.Combine(Root, f);
            if (File.Exists(path)) File.Delete(path);
        }
        if (Directory.Exists(Path.Combine(Root, "backups"))) Directory.Delete(Path.Combine(Root, "backups"), true);
        _host = AppHost.Build(new AppPaths(Root));
        App.Host = _host;
        var log = new StreamWriter(Path.Combine(Root, "avalonia-uitests.log"), append: false) { AutoFlush = true };
        Dispatcher.UIThread.UnhandledException += (_, e) => log.WriteLine("UNHANDLED: " + e.Exception);
        return AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .WithInterFont()
            .LogToTextWriter(log, Avalonia.Logging.LogEventLevel.Warning);
    }

    /// <summary>Runs an async operation on the UI thread while pumping the dispatcher.</summary>
    public static T Run<T>(Func<Task<T>> action, int timeoutSeconds = 180)
    {
        var task = action();
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (!task.IsCompleted)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("UI operation timed out");
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(15);
        }
        return task.GetAwaiter().GetResult();
    }

    public static void Run(Func<Task> action, int timeoutSeconds = 180) => Run(async () =>
    {
        await action();
        return 0;
    }, timeoutSeconds);

    public static void Settle(int rounds = 10)
    {
        for (var i = 0; i < rounds; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(25);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "RosterControl.sln"))) dir = dir.Parent;
        return dir?.FullName ?? AppContext.BaseDirectory;
    }
}
