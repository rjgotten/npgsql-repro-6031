using Microsoft.Extensions.Options;
using Repro.Data;

namespace Repro;

/// <summary>
/// Runs <see cref="ReproOptions.Concurrency"/> loops in parallel. Each iteration creates a DI scope, runs one query
/// through the scoped repository, and lets the scope dispose the DbContext, as in the issue's background worker.
/// </summary>
public sealed class ReproWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<ReproOptions> options,
    DisposalDiagnostics diagnostics,
    IHostApplicationLifetime lifetime,
    ILogger<ReproWorker> logger) : BackgroundService
{
    private readonly ReproOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Starting {Concurrency} loops.", _options.Concurrency);

        var loops = Enumerable.Range(0, _options.Concurrency)
            .Select(_ => Task.Run(() => RunLoopAsync(stoppingToken), CancellationToken.None))
            .ToArray();

        if (_options.Duration is { } duration)
        {
            try
            {
                await Task.Delay(duration, stoppingToken);
                logger.LogInformation("Configured duration of {Duration} elapsed; stopping.", duration);
                lifetime.StopApplication();
            }
            catch (OperationCanceledException)
            {
                // Stopped before the duration elapsed.
            }
        }

        await Task.WhenAll(loops);
    }

    private async Task RunLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunIterationAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                diagnostics.Cancellation();
            }
            catch (Exception exception)
            {
                diagnostics.IterationError();
                if (diagnostics.TryTakeLogBudget(LogCategory.IterationError))
                {
                    logger.LogError(exception, "Iteration failed with {ExceptionType}.", exception.GetType().Name);
                }
            }

            diagnostics.Iteration();

            if (_options.Interval > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(_options.Interval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task RunIterationAsync(CancellationToken stoppingToken)
    {
        // Declared before the scope so it outlives it: a pending cancellation can still fire while the scope is
        // disposing the DbContext.
        using var queryCts = CreateQueryCancellation(stoppingToken);
        var cancellationToken = queryCts.Token;

        if (_options.ScopeDisposal == ScopeDisposalKind.Async)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            await QueryAsync(scope.ServiceProvider, cancellationToken);
        }
        else
        {
            using var scope = scopeFactory.CreateScope();
            await QueryAsync(scope.ServiceProvider, cancellationToken);
        }
    }

    private CancellationTokenSource CreateQueryCancellation(CancellationToken stoppingToken)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        if (Random.Shared.NextDouble() < _options.CancelProbability)
        {
            cts.CancelAfter(TimeSpan.FromMilliseconds(Random.Shared.Next(0, _options.MaxCancelDelayMs + 1)));
        }

        return cts;
    }

    private async Task QueryAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var context = services.GetRequiredService<TranslationsContext>();
        var lease = context.ContextId.Lease;

        if (_options.AliasRegistration)
        {
            // Same instance under a second service type: the scope now tracks it twice.
            services.GetRequiredService<ITranslationsContext>();
        }

        try
        {
            var repository = services.GetRequiredService<ITranslationsRepository>();
            var tenantId = Seeder.TenantId(Random.Shared.Next(_options.Tenants));

            if (Random.Shared.NextDouble() < _options.AbandonReaderProbability)
            {
                diagnostics.AbandonedReader();
                await repository.ReadSomeAndAbandonAsync(tenantId, rows: 3, cancellationToken);
            }
            else
            {
                await repository.GetTranslationsAsync(tenantId, cancellationToken);
            }
        }
        finally
        {
            if (Random.Shared.NextDouble() < _options.ExtraDisposeProbability)
            {
                diagnostics.ExtraDisposal();
                await context.DisposeAsync();
            }

            CheckLease(context, lease);
        }
    }

    /// <summary>
    /// A pooled context whose lease number changed while this scope still holds it has been returned to the pool and
    /// rented by someone else: two owners now share one instance, its connection and any open reader.
    /// </summary>
    private void CheckLease(TranslationsContext context, int expectedLease)
    {
        var currentLease = context.ContextId.Lease;
        if (currentLease == expectedLease)
        {
            return;
        }

        diagnostics.StolenLease();
        if (diagnostics.TryTakeLogBudget(LogCategory.StolenLease))
        {
            logger.LogError(
                "STOLEN LEASE: TranslationsContext #{ContextId} was rented as lease {Expected} by this scope but is now " +
                "on lease {Current}, before this scope ended. Another owner is using the same instance.",
                context.InstanceId,
                expectedLease,
                currentLease);
        }
    }
}
