using Microsoft.EntityFrameworkCore;

namespace Api.Infrastructure;

public static class DatabaseStartup
{
    /// <summary>
    /// Applies migrations and seeds on startup. Auto-migrating is the wrong default for a real
    /// production service, and the right one here: the promise is "clone, compose up, working
    /// demo with data", and there is nobody to run a migration step in between.
    /// </summary>
    public static async Task MigrateAndSeedAsync(this WebApplication app)
    {
        if (!app.Configuration.GetValue("Database:AutoMigrate", defaultValue: true))
        {
            return;
        }

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var clock = scope.ServiceProvider.GetRequiredService<IClock>();

        await db.Database.MigrateAsync();
        await DatabaseSeeder.SeedAsync(db, clock);

        app.Logger.LogInformation("Database ready and seeded.");
    }
}
