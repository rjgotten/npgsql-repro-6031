using Microsoft.Extensions.Options;
using Repro.Data;

namespace Repro;

/// <summary>Logs one line of counters every <see cref="ReproOptions.SummaryInterval"/>, and a final one at shutdown.</summary>
public sealed class SummaryReporter(
    DisposalDiagnostics diagnostics,
    IOptions<ReproOptions> options,
    ILogger<SummaryReporter> logger) : BackgroundService
{
    private DiagnosticsSnapshot _previous;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.SummaryInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                Report("Summary");
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down; StopAsync writes the final summary.
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        Report("Final summary");
    }

    private void Report(string label)
    {
        var s = diagnostics.Snapshot();
        var delta = s.Iterations - _previous.Iterations;
        _previous = s;

        var anomalies = s.ConcurrentDoubleDisposals + s.SequentialDoubleDisposals + s.StolenLeases;
        logger.Log(
            anomalies > 0 ? LogLevel.Warning : LogLevel.Information,
            "{Label}: iterations={Iterations} (+{Delta}) contexts={Contexts} leases={Leases} " +
            "firstDisposals={FirstDisposals} poolTeardowns={PoolTeardowns} extraDisposals={ExtraDisposals} " +
            "cancellations={Cancellations} iterationErrors={IterationErrors} abandonedReaders={AbandonedReaders} | " +
            "doubleDisposals concurrent={Concurrent} sequential={Sequential} stolenLeases={StolenLeases} | " +
            "disposeExceptions objectDisposed={ObjectDisposed} invalidOperation={InvalidOperation} other={Other}",
            label,
            s.Iterations,
            delta,
            s.ContextsCreated,
            s.LeasesRented,
            s.FirstDisposals,
            s.PoolTeardownDisposals,
            s.ExtraDisposals,
            s.Cancellations,
            s.IterationErrors,
            s.AbandonedReaders,
            s.ConcurrentDoubleDisposals,
            s.SequentialDoubleDisposals,
            s.StolenLeases,
            s.DisposeObjectDisposedExceptions,
            s.DisposeInvalidOperationExceptions,
            s.DisposeOtherExceptions);
    }
}
