namespace Repro;

public enum ContainerKind
{
    /// <summary>Microsoft.Extensions.DependencyInjection, the Generic Host default.</summary>
    MsDi,

    /// <summary>Autofac via <c>AutofacServiceProviderFactory</c>.</summary>
    Autofac,
}

public enum ContextRegistrationKind
{
    /// <summary><c>AddDbContext</c>: a fresh context per scope, constructed and disposed by the container.</summary>
    Plain,

    /// <summary>
    /// <c>AddDbContextPool</c>: the scope owns both the context (non-standalone lease, so its own Dispose is a no-op)
    /// and a <c>ScopedDbContextLease</c> whose disposal returns the context to the pool.
    /// </summary>
    Pool,

    /// <summary>
    /// <c>AddPooledDbContextFactory</c>: the scoped context comes from <c>factory.CreateDbContext()</c> with a standalone
    /// lease, so the context's own Dispose returns it to the pool.
    /// </summary>
    PooledFactory,
}

public enum ScopeDisposalKind
{
    /// <summary><c>await using var scope = CreateAsyncScope()</c>: the container calls <c>DbContext.DisposeAsync</c>.</summary>
    Async,

    /// <summary><c>using var scope = CreateScope()</c>: the container calls <c>DbContext.Dispose</c>.</summary>
    Sync,
}

/// <summary>What a second (or later) disposal call does after the gate has detected and logged it.</summary>
public enum DoubleDisposeMode
{
    /// <summary>Call the base disposal again and let any exception propagate to the caller.</summary>
    PassThrough,

    /// <summary>Call the base disposal again, but log, count and swallow any exception it throws.</summary>
    PassThroughSwallow,

    /// <summary>Return immediately without calling the base disposal.</summary>
    Skip,
}

public sealed class ReproOptions
{
    public const string SectionName = "Repro";

    public ContainerKind Container { get; set; } = ContainerKind.MsDi;

    public ContextRegistrationKind ContextRegistration { get; set; } = ContextRegistrationKind.Plain;

    /// <summary>
    /// Also register the context as <c>ITranslationsContext</c> through a factory, and resolve it under both types in
    /// each scope. The container then tracks the same instance twice and disposes it twice.
    /// </summary>
    public bool AliasRegistration { get; set; }

    /// <summary>
    /// Chance (0..1) that application code disposes the injected context itself after the query, as an
    /// <c>await using</c> over a DI-owned context would. The scope then disposes it again.
    /// </summary>
    public double ExtraDisposeProbability { get; set; }

    /// <summary>Number of independent scope/query loops running in parallel.</summary>
    public int Concurrency { get; set; } = 32;

    public ScopeDisposalKind ScopeDisposal { get; set; } = ScopeDisposalKind.Async;

    public DoubleDisposeMode DoubleDisposeMode { get; set; } = DoubleDisposeMode.PassThrough;

    /// <summary>Chance (0..1) that an iteration's query gets a token that cancels after a short random delay.</summary>
    public double CancelProbability { get; set; } = 0.2;

    /// <summary>Upper bound (inclusive) of the random cancellation delay, in milliseconds.</summary>
    public int MaxCancelDelayMs { get; set; } = 5;

    /// <summary>
    /// Chance (0..1) that an iteration reads a few rows and abandons the enumerator without disposing it, so the data
    /// reader is still open when the scope disposes the DbContext. Only effective with <see cref="RetryOnFailure"/>
    /// off: a retrying execution strategy makes EF buffer the whole result set and close the reader up front.
    /// </summary>
    public double AbandonReaderProbability { get; set; }

    /// <summary>Delay between iterations of each loop. Zero means a tight loop.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.Zero;

    /// <summary>Stop the application after this long. Unset means run until stopped.</summary>
    public TimeSpan? Duration { get; set; }

    public TimeSpan SummaryInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Mirrors <c>EnableRetryOnFailure(1, TimeSpan.FromSeconds(0.5), null)</c> from the issue.</summary>
    public bool RetryOnFailure { get; set; } = true;

    public int SeedRows { get; set; } = 5_000;

    public int Tenants { get; set; } = 10;

    /// <summary>Deliberately double-dispose a context at startup to prove the detector works.</summary>
    public bool SelfTest { get; set; }

    /// <summary>
    /// Capture a stack trace on every first disposal, so a later double disposal can show who disposed first.
    /// The second disposer's stack is always captured.
    /// </summary>
    public bool CaptureFirstDisposerStack { get; set; } = true;

    /// <summary>Per category, how many events are logged in full before only being counted.</summary>
    public int MaxDetailedLogs { get; set; } = 50;
}
