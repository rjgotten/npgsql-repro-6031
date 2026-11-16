// Race: a DI scope ends while work it started on its DbContext is still running on another thread.
//
// This is the shape of the failure behind npgsql/npgsql#6031: a fire-and-forget operation (there, a cache fill
// started on a request's scope and not awaited) still reads through the scope's context when the scope's owner ends
// the scope. The scope's disposal then closes the connection the work is reading from:
//
//     DbContext.DisposeAsync → … → NpgsqlConnection.CloseAsync → NpgsqlDataReader.Close → Consume → NextResult
//
// racing the work's own reads on the same connection. Every iteration does that once. Valid bystander contexts on
// the same pools show what the damaged connections do to correct code.
//
// Run:   docker compose run --rm race [--registration plain|pool] [--seconds 5] [--quiet 20] [--loops 16] [--bystanders 8] [--pool-size 300]
// Local: dotnet run --project src/Repro.Race -- [same arguments]   (REPRO_CONNECTION, default: the compose database)
//   plain: AddDbContext       pool: AddDbContextPool (the context also goes back to EF's pool while still in use)

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Repro;

var arguments = new Arguments(args);
var pooled = arguments.Get("registration", "plain") == "pool";
var scenario = new Scenario(arguments, defaultTriggerSeconds: 5);

var services = Scenario.CreateServices();
if (pooled)
{
    services.AddDbContextPool<ReproContext>(options => options.UseNpgsql(scenario.DataSource));
}
else
{
    services.AddDbContext<ReproContext>(options => options.UseNpgsql(scenario.DataSource));
}

var provider = services.BuildServiceProvider(validateScopes: true);
var diagnostics = provider.GetRequiredService<Diagnostics>();

var exitCode = await scenario.RunAsync(
    provider,
    $"Race ({(pooled ? "AddDbContextPool" : "AddDbContext")}): a scope ends while work on its context still runs",
    async _ =>
    {
        var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<ReproContext>();

        // Fire-and-forget: work on the scope's context that the scope's owner does not wait for.
        var operation = context.TrackOperation();
        var work = Task.Run(async () =>
        {
            using (operation)
            {
                await context.Database
                    .SqlQueryRaw<string>("SELECT repeat('x', 100) || g AS \"Value\" FROM generate_series(1, 2000) g")
                    .ToListAsync();
            }
        });

        // The owner is done and ends the scope, while the work may still be reading through the context.
        await Task.Delay(Random.Shared.Next(0, 3));
        var end = scope.DisposeAsync().AsTask();

        var both = Task.WhenAll(work, end);
        if (await Task.WhenAny(both, Task.Delay(TimeSpan.FromSeconds(2))) != both)
        {
            // One side is hung, typically a read on a connection that another owner now drives.
            diagnostics.Hung();
            scenario.Abandon(both.ContinueWith(_ => Record(work, end), TaskScheduler.Default));
            return;
        }

        Record(work, end);
    });

// Exit without disposing the provider or data source: disposing damaged connections can block.
Environment.Exit(exitCode);

void Record(Task work, Task end)
{
    if (work.Exception?.GetBaseException() is { } workException)
    {
        diagnostics.TriggerFailure("work", workException);
    }

    if (end.Exception?.GetBaseException() is { } endException)
    {
        diagnostics.TriggerFailure("scope end", endException);
    }
}
