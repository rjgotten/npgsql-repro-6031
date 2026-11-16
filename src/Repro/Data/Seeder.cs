using Microsoft.EntityFrameworkCore;

namespace Repro.Data;

public static class Seeder
{
    private const int BatchSize = 1_000;

    public static string TenantId(int index) => $"tenant-{index:D2}";

    public static async Task InitializeAsync(
        IServiceProvider services, ReproOptions options, ILogger logger, CancellationToken cancellationToken)
    {
        await WaitForDatabaseAsync(services, logger, cancellationToken);

        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<TranslationsContext>();

        await context.Database.EnsureCreatedAsync(cancellationToken);

        var existing = await context.Translations.CountAsync(cancellationToken);
        if (existing > 0)
        {
            logger.LogInformation("Database already holds {Rows} translations; skipping seed.", existing);
            return;
        }

        // Long-ish values so each reader has a meaningful amount of data left to consume when it is closed early.
        var filler = new string('x', 200);
        for (var offset = 0; offset < options.SeedRows; offset += BatchSize)
        {
            var count = Math.Min(BatchSize, options.SeedRows - offset);
            context.Translations.AddRange(Enumerable.Range(offset, count).Select(i => new Translation
            {
                TenantId = TenantId(i % options.Tenants),
                Key = $"key.{i:D6}",
                Value = $"{i:D6} {filler}",
            }));

            await context.SaveChangesAsync(cancellationToken);
            context.ChangeTracker.Clear();
        }

        logger.LogInformation(
            "Seeded {Rows} translations across {Tenants} tenants.", options.SeedRows, options.Tenants);
    }

    private static async Task WaitForDatabaseAsync(
        IServiceProvider services, ILogger logger, CancellationToken cancellationToken)
    {
        const int maxAttempts = 30;
        for (var attempt = 1; ; attempt++)
        {
            await using var scope = services.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<TranslationsContext>();

            if (await context.Database.CanConnectAsync(cancellationToken))
            {
                return;
            }

            if (attempt == maxAttempts)
            {
                throw new InvalidOperationException($"Database not reachable after {maxAttempts} attempts.");
            }

            logger.LogInformation("Database not reachable yet (attempt {Attempt}/{Max}).", attempt, maxAttempts);
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
    }
}
