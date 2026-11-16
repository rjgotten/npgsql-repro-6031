using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Repro;

/// <summary>Command-line settings: <c>--name value</c> pairs.</summary>
public sealed class Arguments(string[] args)
{
    public string Get(string name, string fallback)
    {
        var index = Array.IndexOf(args, "--" + name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback;
    }

    public int Get(string name, int fallback) => int.TryParse(Get(name, ""), out var value) ? value : fallback;
}

/// <summary>
/// Runs a scenario's trigger next to the bystanders, in three phases (trigger, draining, quiet), then probes how many
/// Npgsql pool slots are still checked out once all work has stopped.
/// </summary>
public sealed class Scenario
{
    private readonly ConcurrentBag<Task> _abandoned = [];

    public Scenario(Arguments arguments, int defaultTriggerSeconds)
    {
        TriggerSeconds = arguments.Get("seconds", defaultTriggerSeconds);
        QuietSeconds = arguments.Get("quiet", 20);
        Loops = arguments.Get("loops", 16);
        BystanderCount = arguments.Get("bystanders", 8);
        MaxPoolSize = arguments.Get("pool-size", 300);

        var connectionString = Environment.GetEnvironmentVariable("REPRO_CONNECTION")
            ?? "Host=localhost;Port=5432;Database=repro;Username=postgres;Password=postgres";

        // The pool is sized well above what a run can leak, so leaked slots don't starve the bystanders. A short
        // command timeout bounds how long a read on a hijacked connection can hang.
        DataSource = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(connectionString)
        {
            CommandTimeout = 5,
            MaxPoolSize = MaxPoolSize,
        }.ConnectionString);
    }

    public int TriggerSeconds { get; }
    public int QuietSeconds { get; }
    public int Loops { get; }
    public int BystanderCount { get; }
    public int MaxPoolSize { get; }
    public NpgsqlDataSource DataSource { get; }

    /// <summary>Stock MS DI with console logging and the shared diagnostics; the scenario adds its registrations.</summary>
    public static IServiceCollection CreateServices()
    {
        var services = new ServiceCollection();
        services.AddLogging(logging => logging
            .AddSimpleConsole(console => console.TimestampFormat = "HH:mm:ss.fff ")
            // The scenarios log failures themselves; EF's own per-query error logs would only repeat them.
            .AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Critical));
        services.AddSingleton<Diagnostics>();
        return services;
    }

    /// <summary>Hands trigger work that has not finished in time to the draining phase instead of blocking on it.</summary>
    public void Abandon(Task work) => _abandoned.Add(work);

    /// <summary>Runs everything and returns the exit code: 1 when correct code failed or pool slots leaked.</summary>
    public async Task<int> RunAsync(IServiceProvider provider, string description, Func<CancellationToken, Task> iteration)
    {
        var diagnostics = provider.GetRequiredService<Diagnostics>();
        var logger = provider.GetRequiredService<ILogger<Scenario>>();

        logger.LogInformation(
            "{Description}. Trigger {Seconds}s with {Loops} loops, {Bystanders} bystanders, then {Quiet}s quiet. Npgsql pool size {PoolSize}.",
            description, TriggerSeconds, Loops, BystanderCount, QuietSeconds, MaxPoolSize);

        using var triggerStop = new CancellationTokenSource(TimeSpan.FromSeconds(TriggerSeconds));
        using var stopAll = new CancellationTokenSource();

        diagnostics.Phase = Phase.Trigger;
        var bystanders = Bystanders.Start(provider, diagnostics, BystanderCount, stopAll.Token, out var bystanderAt);
        var loops = Enumerable.Range(0, Loops)
            .Select(_ => Task.Run(() => TriggerLoopAsync(diagnostics, iteration, triggerStop.Token)))
            .ToArray();

        // Every wait is bounded: calls on a hijacked connection can block indefinitely, and that is reported rather
        // than waited on.
        await WaitBoundedAsync(Task.WhenAll(loops), TriggerSeconds + 15);
        logger.LogInformation("Trigger stopped; loops still blocked 15s later: {Blocked}", loops.Count(t => !t.IsCompleted));

        diagnostics.Phase = Phase.Draining;
        var abandoned = _abandoned.ToArray();
        await WaitBoundedAsync(Task.WhenAll(abandoned.Select(t => t.ContinueWith(_ => { }, TaskScheduler.Default))), 15);
        logger.LogInformation("Drained abandoned trigger work: {Total}, still running after 15s: {Running}", abandoned.Length, abandoned.Count(t => !t.IsCompleted));

        diagnostics.Phase = Phase.Quiet;
        await Task.Delay(TimeSpan.FromSeconds(QuietSeconds));
        stopAll.Cancel();
        await WaitBoundedAsync(Task.WhenAll(bystanders), 15);
        var blocked = Enumerable.Range(0, bystanders.Length).Where(i => !bystanders[i].IsCompleted).ToArray();
        if (blocked.Length == 0)
        {
            logger.LogInformation("Bystanders stopped");
        }
        else
        {
            logger.LogError(
                "BYSTANDERS BLOCKED: {Count} of {Total} valid contexts are still blocked 15s after being told to stop, in: {Where}",
                blocked.Length, bystanders.Length,
                string.Join(", ", blocked.GroupBy(i => bystanderAt[i]).Select(g => $"{g.Key} ×{g.Count()}")));
        }

        // Nothing references the trigger's contexts and connections any more. Collecting them shows whether the pool
        // reclaims slots from connections that were dropped without a successful close.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var free = await ProbeFreeSlotsAsync();

        diagnostics.PrintSummary(free, MaxPoolSize);
        return diagnostics.BystanderFailures > 0 || free < MaxPoolSize ? 1 : 0;
    }

    private static async Task TriggerLoopAsync(Diagnostics diagnostics, Func<CancellationToken, Task> iteration, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await iteration(stop);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                diagnostics.TriggerFailure("iteration", exception);
            }

            diagnostics.Iteration();
        }
    }

    // Opens connections one at a time until the pool refuses within a short wait, then releases them.
    private async Task<int> ProbeFreeSlotsAsync()
    {
        var held = new List<NpgsqlConnection>();
        try
        {
            while (held.Count < MaxPoolSize)
            {
                using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try
                {
                    held.Add(await DataSource.OpenConnectionAsync(wait.Token));
                }
                catch (Exception)
                {
                    break;
                }
            }

            return held.Count;
        }
        finally
        {
            foreach (var connection in held)
            {
                await connection.DisposeAsync();
            }
        }
    }

    private static Task WaitBoundedAsync(Task task, int seconds) => Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(seconds)));
}
