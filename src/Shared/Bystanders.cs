using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Repro;

/// <summary>
/// Valid contexts: each loop creates its own scope, resolves its own <see cref="ReproContext"/>, awaits one query,
/// checks every row and lets the scope dispose the context once. They share nothing with the scenario's trigger
/// except the registration, and so EF's context pool (where there is one) and Npgsql's connection pool.
/// </summary>
public static class Bystanders
{
    private const int Rows = 1000;
    private const long Sum = Rows * (Rows + 1L) / 2;

    /// <summary>Starts the loops. <paramref name="locations"/> receives where each loop currently is.</summary>
    public static Task[] Start(IServiceProvider provider, Diagnostics diagnostics, int count, CancellationToken stop, out string[] locations)
    {
        var at = locations = new string[count];
        return Enumerable.Range(0, count).Select(i => Task.Run(() => LoopAsync(provider, diagnostics, at, i, stop))).ToArray();
    }

    private static async Task LoopAsync(IServiceProvider provider, Diagnostics diagnostics, string[] at, int id, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            var phase = diagnostics.Phase;
            try
            {
                at[id] = "resolving the context";
                await using var scope = provider.CreateAsyncScope();
                var context = scope.ServiceProvider.GetRequiredService<ReproContext>();

                at[id] = "running the query";
                var values = await context.Database
                    .SqlQueryRaw<int>($"SELECT g AS \"Value\" FROM generate_series(1, {Rows}) g")
                    .ToListAsync(stop);

                // The scope disposes the context when this block ends.
                at[id] = "disposing the scope";

                if (values.Count != Rows || values.Sum(v => (long)v) != Sum)
                {
                    diagnostics.BystanderWrongResult(phase, $"read {values.Count} rows summing to {values.Sum(v => (long)v)}");
                }
                else
                {
                    diagnostics.BystanderQuery(phase);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                diagnostics.BystanderFailure(phase, exception);
            }
        }
    }
}
