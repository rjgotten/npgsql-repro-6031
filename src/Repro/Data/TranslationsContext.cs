using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Repro.Data;

/// <summary>
/// DbContext whose <see cref="DisposeAsync"/> and <see cref="Dispose"/> pass through an <see cref="Interlocked"/> gate.
/// The first disposal call wins the gate. Any later call, from either method, is a double disposal and gets logged with
/// the stack traces of both disposers. It is classified as <c>Concurrent</c> when the first disposal was still running
/// and as <c>Sequential</c> when it had already finished.
/// </summary>
public sealed class TranslationsContext : DbContext, ITranslationsContext
{
    private static long s_nextInstanceId;

    private readonly ILogger<TranslationsContext> _logger;
    private readonly DisposalDiagnostics _diagnostics;
    private readonly ReproOptions _reproOptions;

    // Set once per lease, by the caller that wins the CompareExchange. OnRented clears it for pooled instances.
    private DisposerInfo? _firstDisposer;
    private volatile bool _firstDisposalCompleted;

    public TranslationsContext(
        DbContextOptions<TranslationsContext> options,
        ILogger<TranslationsContext> logger,
        DisposalDiagnostics diagnostics,
        IOptions<ReproOptions> reproOptions)
        : base(options)
    {
        _logger = logger;
        _diagnostics = diagnostics;
        _reproOptions = reproOptions.Value;
        InstanceId = Interlocked.Increment(ref s_nextInstanceId);
        diagnostics.ContextCreated();
    }

    public long InstanceId { get; }

    public DbSet<Translation> Translations => Set<Translation>();

    /// <summary>
    /// Runs inside the winning <see cref="DisposeAsync"/> before the base disposal. <see cref="SelfTest"/> uses it to
    /// hold the first disposal open so a second call deterministically overlaps it.
    /// </summary>
    internal Func<Task>? BeforeFirstBaseDisposeAsync { get; set; }

    /// <summary>
    /// Re-arms the gate when <see cref="TrackingDbContextPool"/> hands this instance out for a new lease. This is not
    /// atomic with respect to a stray disposal racing the rent: such a disposal is still detected, but may be attributed
    /// to the new lease, making the new owner's legitimate disposal look like the repeat.
    /// </summary>
    internal void OnRented()
    {
        BeforeFirstBaseDisposeAsync = null;
        _firstDisposalCompleted = false;
        Volatile.Write(ref _firstDisposer, null);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Translation>(entity =>
        {
            entity.ToTable("translations");
            entity.HasIndex(t => t.TenantId);
        });
    }

    public override async ValueTask DisposeAsync()
    {
        if (TrackingDbContextPool.IsPoolTeardown)
        {
            LogPoolTeardown(nameof(DisposeAsync));
            await base.DisposeAsync();
            return;
        }

        var me = DisposerInfo.Capture(nameof(DisposeAsync), _reproOptions.CaptureFirstDisposerStack);
        var first = Interlocked.CompareExchange(ref _firstDisposer, me, null);

        if (first is null)
        {
            _diagnostics.FirstDisposal();
            try
            {
                if (BeforeFirstBaseDisposeAsync is { } hook)
                {
                    await hook();
                }

                await base.DisposeAsync();
            }
            catch (Exception exception)
            {
                RecordDisposeException(exception, me, isFirstDisposer: true);
                throw;
            }
            finally
            {
                _firstDisposalCompleted = true;
            }

            return;
        }

        if (!ReportDoubleDisposal(first, me))
        {
            return;
        }

        try
        {
            await base.DisposeAsync();
        }
        catch (Exception exception)
        {
            RecordDisposeException(exception, me, isFirstDisposer: false);
            if (_reproOptions.DoubleDisposeMode == DoubleDisposeMode.PassThrough)
            {
                throw;
            }
        }
    }

    public override void Dispose()
    {
        if (TrackingDbContextPool.IsPoolTeardown)
        {
            LogPoolTeardown(nameof(Dispose));
            base.Dispose();
            return;
        }

        var me = DisposerInfo.Capture(nameof(Dispose), _reproOptions.CaptureFirstDisposerStack);
        var first = Interlocked.CompareExchange(ref _firstDisposer, me, null);

        if (first is null)
        {
            _diagnostics.FirstDisposal();
            try
            {
                base.Dispose();
            }
            catch (Exception exception)
            {
                RecordDisposeException(exception, me, isFirstDisposer: true);
                throw;
            }
            finally
            {
                _firstDisposalCompleted = true;
            }

            return;
        }

        if (!ReportDoubleDisposal(first, me))
        {
            return;
        }

        try
        {
            base.Dispose();
        }
        catch (Exception exception)
        {
            RecordDisposeException(exception, me, isFirstDisposer: false);
            if (_reproOptions.DoubleDisposeMode == DoubleDisposeMode.PassThrough)
            {
                throw;
            }
        }
    }

    /// <summary>Counts and logs a gate loss. Returns whether the caller should still run the base disposal.</summary>
    private bool ReportDoubleDisposal(DisposerInfo first, DisposerInfo second)
    {
        var concurrent = !_firstDisposalCompleted;
        _diagnostics.DoubleDisposal(concurrent);

        if (_diagnostics.TryTakeLogBudget(LogCategory.DoubleDisposal))
        {
            // The first disposer's stack may be absent when CaptureFirstDisposerStack is off. The second disposer's
            // stack is always worth having, so capture it here if Capture skipped it.
            var secondStack = second.Stack ?? new StackTrace(1, fNeedFileInfo: true);

            _logger.LogWarning(
                "DOUBLE DISPOSAL of TranslationsContext #{ContextId} lease {Lease} ({Kind}): {SecondCaller} on thread {SecondThread} " +
                "came {ElapsedMs:F3} ms after {FirstCaller} on thread {FirstThread}. DoubleDisposeMode={Mode}." +
                "{NewLine}--- second disposer ---{NewLine}{SecondStack}" +
                "{NewLine}--- first disposer ---{NewLine}{FirstStack}",
                InstanceId,
                ContextId.Lease,
                concurrent ? "Concurrent" : "Sequential",
                second.Caller,
                second.ThreadId,
                Stopwatch.GetElapsedTime(first.Timestamp, second.Timestamp).TotalMilliseconds,
                first.Caller,
                first.ThreadId,
                _reproOptions.DoubleDisposeMode,
                Environment.NewLine,
                Environment.NewLine,
                secondStack,
                Environment.NewLine,
                Environment.NewLine,
                first.Stack?.ToString() ?? "(not captured; set Repro:CaptureFirstDisposerStack=true)");
        }

        return _reproOptions.DoubleDisposeMode != DoubleDisposeMode.Skip;
    }

    private void LogPoolTeardown(string caller)
    {
        _diagnostics.PoolTeardownDisposal();
        _logger.LogDebug(
            "{Caller} of TranslationsContext #{ContextId} issued by the pool itself (overflow or pool disposal); not gated.",
            caller,
            InstanceId);
    }

    private void RecordDisposeException(Exception exception, DisposerInfo disposer, bool isFirstDisposer)
    {
        _diagnostics.DisposeException(exception);

        if (_diagnostics.TryTakeLogBudget(LogCategory.DisposeException))
        {
            _logger.LogError(
                exception,
                "{Caller} of TranslationsContext #{ContextId} threw {ExceptionType} on thread {ThreadId} ({Role} disposer).",
                disposer.Caller,
                InstanceId,
                exception.GetType().Name,
                disposer.ThreadId,
                isFirstDisposer ? "first" : "repeat");
        }
    }

    private sealed record DisposerInfo(string Caller, int ThreadId, long Timestamp, StackTrace? Stack)
    {
        // Skips the Capture frame itself, so the trace starts at Dispose/DisposeAsync.
        public static DisposerInfo Capture(string caller, bool captureStack) => new(
            caller,
            Environment.CurrentManagedThreadId,
            Stopwatch.GetTimestamp(),
            captureStack ? new StackTrace(1, fNeedFileInfo: true) : null);
    }
}
