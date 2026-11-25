using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RosterControl.Core.Model;

namespace RosterControl.Data;

/// <summary>
/// Small key/value UI preferences (window placement, last tab, filters). Kept in memory and written to SQLite in the
/// background (debounced), so the UI never blocks on the database.
/// </summary>
public sealed class PreferencesStore : IDisposable
{
    private readonly IDbContextFactory<RosterDbContext> _factory;
    private readonly RosterRepository _repository;
    private readonly ILogger<PreferencesStore> _logger;
    private readonly ConcurrentDictionary<string, string> _values = new();
    private readonly ConcurrentDictionary<string, byte> _dirty = new();
    private readonly Timer _flushTimer;
    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private bool _loaded;

    public PreferencesStore(IDbContextFactory<RosterDbContext> factory, RosterRepository repository, ILogger<PreferencesStore> logger)
    {
        _factory = factory;
        _repository = repository;
        _logger = logger;
        _flushTimer = new Timer(_ => _ = FlushAsync(CancellationToken.None), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Loads all preferences synchronously at startup (single small table).</summary>
    public void Load()
    {
        if (_loaded) return;
        try
        {
            _repository.InitializeAsync().GetAwaiter().GetResult();
            using var db = _factory.CreateDbContext();
            foreach (var p in db.Preferences.AsNoTracking()) _values[p.Key] = p.ValueJson;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load preferences; defaults will be used");
        }
        _loaded = true;
    }

    public T Get<T>(string key, T defaultValue)
    {
        if (!_loaded) Load();
        if (!_values.TryGetValue(key, out var json)) return defaultValue;
        try
        {
            return JsonSerializer.Deserialize<T>(json) ?? defaultValue;
        }
        catch (JsonException)
        {
            return defaultValue;
        }
    }

    public void Set<T>(string key, T value)
    {
        var json = JsonSerializer.Serialize(value);
        if (_values.TryGetValue(key, out var existing) && existing == json) return;
        _values[key] = json;
        _dirty[key] = 0;
        _flushTimer.Change(TimeSpan.FromMilliseconds(600), Timeout.InfiniteTimeSpan);
    }

    public async Task FlushAsync(CancellationToken ct)
    {
        if (_dirty.IsEmpty) return;
        await _flushGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var keys = _dirty.Keys.ToList();
            foreach (var k in keys) _dirty.TryRemove(k, out _);
            await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var existing = await db.Preferences.Where(p => keys.Contains(p.Key)).ToDictionaryAsync(p => p.Key, ct).ConfigureAwait(false);
            foreach (var key in keys)
            {
                if (!_values.TryGetValue(key, out var value)) continue;
                if (existing.TryGetValue(key, out var row)) row.ValueJson = value;
                else db.Preferences.Add(new Preference { Key = key, ValueJson = value });
            }
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not save preferences");
        }
        finally
        {
            _flushGate.Release();
        }
    }

    public void Dispose()
    {
        _flushTimer.Dispose();
        _flushGate.Dispose();
    }
}
