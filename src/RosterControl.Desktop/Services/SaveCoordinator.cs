using System.Collections.Concurrent;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;

namespace RosterControl.Desktop.Services;

/// <summary>
/// Debounced auto-save. Editors call <see cref="Schedule"/> on every change; the save runs once the user pauses.
/// Pending saves are flushed when navigating away and when the application closes, so nothing is lost.
/// </summary>
public sealed class SaveCoordinator
{
    private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(450);
    private readonly ConcurrentDictionary<object, Pending> _pending = new();
    private readonly ILogger<SaveCoordinator> _logger;
    private int _saving;

    public SaveCoordinator(ILogger<SaveCoordinator> logger) => _logger = logger;

    /// <summary>Raised on the UI thread when saving starts/stops (for the "Saving…/Saved" indicator).</summary>
    public event EventHandler<bool>? SavingChanged;

    public bool HasPending => !_pending.IsEmpty;

    /// <param name="key">Identity of the thing being saved; a newer schedule for the same key replaces the older one.</param>
    public void Schedule(object key, Func<Task> save)
    {
        var pending = _pending.AddOrUpdate(key, _ => new Pending(save), (_, p) =>
        {
            p.Save = save;
            return p;
        });
        pending.Timer ??= new DispatcherTimer(Delay, DispatcherPriority.Background, (_, _) => _ = RunAsync(key));
        pending.Timer.Stop();
        pending.Timer.Start();
    }

    public async Task FlushAsync()
    {
        foreach (var key in _pending.Keys.ToList()) await RunAsync(key);
    }

    /// <summary>Blocking flush for application exit.</summary>
    public void FlushBlocking()
    {
        foreach (var (key, pending) in _pending.ToList())
        {
            _pending.TryRemove(key, out _);
            pending.Timer?.Stop();
            try
            {
                Task.Run(pending.Save).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Save on exit failed");
            }
        }
    }

    private async Task RunAsync(object key)
    {
        if (!_pending.TryRemove(key, out var pending)) return;
        pending.Timer?.Stop();
        if (Interlocked.Increment(ref _saving) == 1) SavingChanged?.Invoke(this, true);
        try
        {
            await pending.Save();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Auto-save failed");
        }
        finally
        {
            if (Interlocked.Decrement(ref _saving) == 0) SavingChanged?.Invoke(this, false);
        }
    }

    private sealed class Pending(Func<Task> save)
    {
        public Func<Task> Save { get; set; } = save;
        public DispatcherTimer? Timer { get; set; }
    }
}
