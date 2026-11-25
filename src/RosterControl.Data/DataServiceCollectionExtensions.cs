using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RosterControl.Core.Abstractions;

namespace RosterControl.Data;

public static class DataServiceCollectionExtensions
{
    public static IServiceCollection AddRosterData(this IServiceCollection services)
    {
        services.AddDbContextFactory<RosterDbContext>((sp, options) =>
        {
            var paths = sp.GetRequiredService<IAppPaths>();
            // Rollback-journal mode (SQLite default) rather than WAL: a single file is safer when the app folder is
            // synced by OneDrive or copied while the app is closed.
            var cs = new SqliteConnectionStringBuilder { DataSource = paths.DatabasePath, Cache = SqliteCacheMode.Private, Pooling = true }.ToString();
            options.UseSqlite(cs);
        });
        services.AddSingleton<RosterRepository>();
        services.AddSingleton<PreferencesStore>();
        services.AddSingleton<BackupService>();
        return services;
    }
}
