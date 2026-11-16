using Microsoft.Extensions.Options;

namespace Repro.Data;

public enum LogCategory
{
    DoubleDisposal,
    DisposeException,
    IterationError,
    StolenLease,
}

public readonly record struct DiagnosticsSnapshot(
    long ContextsCreated,
    long LeasesRented,
    long FirstDisposals,
    long PoolTeardownDisposals,
    long ExtraDisposals,
    long StolenLeases,
    long Iterations,
    long Cancellations,
    long IterationErrors,
    long AbandonedReaders,
    long ConcurrentDoubleDisposals,
    long SequentialDoubleDisposals,
    long DisposeObjectDisposedExceptions,
    long DisposeInvalidOperationExceptions,
    long DisposeOtherExceptions);

/// <summary>Process-wide counters, shared by every context instance and worker loop.</summary>
public sealed class DisposalDiagnostics
{
    private readonly int[] _logBudgets;

    private long _contextsCreated;
    private long _leasesRented;
    private long _firstDisposals;
    private long _poolTeardownDisposals;
    private long _extraDisposals;
    private long _stolenLeases;
    private long _iterations;
    private long _cancellations;
    private long _iterationErrors;
    private long _abandonedReaders;
    private long _concurrentDoubleDisposals;
    private long _sequentialDoubleDisposals;
    private long _disposeObjectDisposedExceptions;
    private long _disposeInvalidOperationExceptions;
    private long _disposeOtherExceptions;

    public DisposalDiagnostics(IOptions<ReproOptions> options)
    {
        _logBudgets = new int[Enum.GetValues<LogCategory>().Length];
        Array.Fill(_logBudgets, options.Value.MaxDetailedLogs);
    }

    public void ContextCreated() => Interlocked.Increment(ref _contextsCreated);

    public void LeaseRented() => Interlocked.Increment(ref _leasesRented);

    /// <summary>A disposal call that won the gate: once per non-pooled instance, or once per lease when pooled.</summary>
    public void FirstDisposal() => Interlocked.Increment(ref _firstDisposals);

    public void PoolTeardownDisposal() => Interlocked.Increment(ref _poolTeardownDisposals);

    public void ExtraDisposal() => Interlocked.Increment(ref _extraDisposals);

    public void StolenLease() => Interlocked.Increment(ref _stolenLeases);

    public void Iteration() => Interlocked.Increment(ref _iterations);

    public void Cancellation() => Interlocked.Increment(ref _cancellations);

    public void IterationError() => Interlocked.Increment(ref _iterationErrors);

    public void AbandonedReader() => Interlocked.Increment(ref _abandonedReaders);

    public void DoubleDisposal(bool concurrent)
        => Interlocked.Increment(ref concurrent ? ref _concurrentDoubleDisposals : ref _sequentialDoubleDisposals);

    public void DisposeException(Exception exception)
    {
        // ObjectDisposedException derives from InvalidOperationException, so it has to be tested first.
        switch (exception)
        {
            case ObjectDisposedException:
                Interlocked.Increment(ref _disposeObjectDisposedExceptions);
                break;
            case InvalidOperationException:
                Interlocked.Increment(ref _disposeInvalidOperationExceptions);
                break;
            default:
                Interlocked.Increment(ref _disposeOtherExceptions);
                break;
        }
    }

    /// <summary>True while the category still has budget for a full, detailed log entry.</summary>
    public bool TryTakeLogBudget(LogCategory category)
        => Interlocked.Decrement(ref _logBudgets[(int)category]) >= 0;

    public DiagnosticsSnapshot Snapshot() => new(
        Interlocked.Read(ref _contextsCreated),
        Interlocked.Read(ref _leasesRented),
        Interlocked.Read(ref _firstDisposals),
        Interlocked.Read(ref _poolTeardownDisposals),
        Interlocked.Read(ref _extraDisposals),
        Interlocked.Read(ref _stolenLeases),
        Interlocked.Read(ref _iterations),
        Interlocked.Read(ref _cancellations),
        Interlocked.Read(ref _iterationErrors),
        Interlocked.Read(ref _abandonedReaders),
        Interlocked.Read(ref _concurrentDoubleDisposals),
        Interlocked.Read(ref _sequentialDoubleDisposals),
        Interlocked.Read(ref _disposeObjectDisposedExceptions),
        Interlocked.Read(ref _disposeInvalidOperationExceptions),
        Interlocked.Read(ref _disposeOtherExceptions));
}
