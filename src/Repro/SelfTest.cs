using Microsoft.EntityFrameworkCore;
using Repro.Data;

namespace Repro;

/// <summary>
/// Deliberately disposes one context three times to prove the gate detects and logs both kinds of double disposal:
/// <list type="number">
/// <item>The first <c>DisposeAsync</c> wins the gate and is held open for a moment.</item>
/// <item>A second <c>DisposeAsync</c> arrives meanwhile: <c>Concurrent</c>.</item>
/// <item>The DI scope disposes the context once more at the end: <c>Sequential</c>.</item>
/// </list>
/// </summary>
public static class SelfTest
{
    public static async Task<bool> RunAsync(IServiceProvider services, ILogger logger, CancellationToken cancellationToken)
    {
        var diagnostics = services.GetRequiredService<DisposalDiagnostics>();
        var before = diagnostics.Snapshot();

        logger.LogInformation("Self-test: disposing one context three times; expect 1 Concurrent + 1 Sequential.");

        await using (var scope = services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<TranslationsContext>();

            // Open and use the connection once, so disposal has real work to do.
            await context.Translations.AsNoTracking().OrderBy(t => t.Id).Take(1).ToArrayAsync(cancellationToken);

            var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            context.BeforeFirstBaseDisposeAsync = async () =>
            {
                firstEntered.SetResult();
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
            };

            var first = Task.Run(async () => await context.DisposeAsync(), cancellationToken);
            await firstEntered.Task;
            var second = context.DisposeAsync().AsTask();

            try
            {
                await Task.WhenAll(first, second);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Self-test: a disposal call threw (counted above).");
            }
        }

        var after = diagnostics.Snapshot();
        var concurrent = after.ConcurrentDoubleDisposals - before.ConcurrentDoubleDisposals;
        var sequential = after.SequentialDoubleDisposals - before.SequentialDoubleDisposals;

        if (concurrent == 1 && sequential == 1)
        {
            logger.LogInformation("Self-test PASSED: detected 1 Concurrent and 1 Sequential double disposal.");
            return true;
        }

        logger.LogError(
            "Self-test FAILED: expected 1 Concurrent + 1 Sequential, got {Concurrent} Concurrent + {Sequential} Sequential.",
            concurrent,
            sequential);
        return false;
    }
}
