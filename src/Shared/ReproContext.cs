using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Repro;

/// <summary>The context as consumers see it through an interface registration.</summary>
public interface IReproContext
{
    DatabaseFacade Database { get; }
}

/// <summary>
/// A DbContext with no model (the scenarios only run raw SQL), instrumented to log the two conditions that lead to
/// two parties driving one Npgsql connection:
/// <list type="bullet">
/// <item>
/// Disposed while in use: a disposal arrives while an operation registered with <see cref="TrackOperation"/> is still
/// running on the context on another thread (the race scenario).
/// </item>
/// <item>
/// Double disposal: a second disposal arrives within the same lease, detected through an
/// <see cref="Interlocked.CompareExchange{T}(ref T, T, T)"/> gate (the alias scenario). It is <c>Concurrent</c> when
/// the first disposal was still running and <c>Sequential</c> when it had finished.
/// </item>
/// </list>
/// Both are logged with the disposer's stack trace. Disposal itself always proceeds into EF unchanged.
/// </summary>
/// <remarks>
/// A pooled instance serves many leases, so the gate is keyed by <see cref="DbContextId.Lease"/>. A stray disposal
/// that arrives after the instance has been rented again carries the new lease number; it then counts as that lease's
/// first disposal, and the new owner's own disposal is the one reported as the repeat. Both stacks are logged either
/// way.
/// </remarks>
public sealed class ReproContext : DbContext, IReproContext
{
    private static long s_nextId;

    private readonly ILogger<ReproContext> _logger;
    private readonly Diagnostics _diagnostics;

    private Disposer? _first;
    private int _operationsInFlight;

    public ReproContext(DbContextOptions<ReproContext> options, ILogger<ReproContext> logger, Diagnostics diagnostics)
        : base(options)
    {
        _logger = logger;
        _diagnostics = diagnostics;
        Id = Interlocked.Increment(ref s_nextId);
    }

    public long Id { get; }

    /// <summary>Marks an operation as running on this context until the returned handle is disposed.</summary>
    public IDisposable TrackOperation()
    {
        Interlocked.Increment(ref _operationsInFlight);
        return new Operation(this);
    }

    public override async ValueTask DisposeAsync()
    {
        var first = OnDisposing(nameof(DisposeAsync));
        try
        {
            await base.DisposeAsync();
        }
        finally
        {
            first?.Complete();
        }
    }

    public override void Dispose()
    {
        var first = OnDisposing(nameof(Dispose));
        try
        {
            base.Dispose();
        }
        finally
        {
            first?.Complete();
        }
    }

    /// <summary>Logs the conditions above. Returns this call's record when it is the lease's first disposal.</summary>
    private Disposer? OnDisposing(string method)
    {
        var me = new Disposer(method, ContextId.Lease, Environment.CurrentManagedThreadId, Stopwatch.GetTimestamp(), new StackTrace(1, fNeedFileInfo: true));

        var inFlight = Volatile.Read(ref _operationsInFlight);
        if (inFlight > 0)
        {
            _diagnostics.DisposedInUse();
            if (_diagnostics.TryLog("disposed-in-use"))
            {
                _logger.LogWarning(
                    "DISPOSED WHILE IN USE: ReproContext #{Id} lease {Lease}: {Method} on thread {Thread} while {InFlight} operation(s) still run on it.{NewLine}{Stack}",
                    Id, me.Lease, method, me.ThreadId, inFlight, Environment.NewLine, me.Stack);
            }
        }

        while (true)
        {
            var first = Volatile.Read(ref _first);
            if (first is not null && first.Lease == me.Lease)
            {
                var concurrent = !first.Completed;
                _diagnostics.DoubleDisposal(concurrent);
                if (_diagnostics.TryLog("double-disposal"))
                {
                    _logger.LogWarning(
                        "DOUBLE DISPOSAL ({Kind}): ReproContext #{Id} lease {Lease}: {Method} on thread {Thread} came {Elapsed:F3} ms after {FirstMethod} on thread {FirstThread}." +
                        "{NewLine}--- second disposer ---{NewLine}{Stack}{NewLine}--- first disposer ---{NewLine}{FirstStack}",
                        concurrent ? "Concurrent" : "Sequential", Id, me.Lease, method, me.ThreadId,
                        Stopwatch.GetElapsedTime(first.Timestamp, me.Timestamp).TotalMilliseconds, first.Method, first.ThreadId,
                        Environment.NewLine, Environment.NewLine, me.Stack, Environment.NewLine, Environment.NewLine, first.Stack);
                }

                return null;
            }

            if (Interlocked.CompareExchange(ref _first, me, first) == first)
            {
                return me;
            }
        }
    }

    private sealed class Disposer(string method, int lease, int threadId, long timestamp, StackTrace stack)
    {
        private volatile bool _completed;

        public string Method { get; } = method;
        public int Lease { get; } = lease;
        public int ThreadId { get; } = threadId;
        public long Timestamp { get; } = timestamp;
        public StackTrace Stack { get; } = stack;
        public bool Completed => _completed;

        public void Complete() => _completed = true;
    }

    private sealed class Operation(ReproContext context) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Interlocked.Decrement(ref context._operationsInFlight);
            }
        }
    }
}
