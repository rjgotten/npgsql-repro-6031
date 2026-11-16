using Microsoft.EntityFrameworkCore;
using Repro.Data;

namespace Repro;

public interface ITranslationsRepository
{
    Task<Translation[]> GetTranslationsAsync(string tenantId, CancellationToken cancellationToken);

    Task<int> ReadSomeAndAbandonAsync(string tenantId, int rows, CancellationToken cancellationToken);
}

/// <summary>The query shape follows the repository code posted in npgsql/npgsql#6031.</summary>
public sealed class SqlTranslationsRepository(TranslationsContext context, ILogger<SqlTranslationsRepository> logger)
    : ITranslationsRepository
{
    public async Task<Translation[]> GetTranslationsAsync(string tenantId, CancellationToken cancellationToken)
    {
        try
        {
            return await context.Translations
                .AsNoTracking()
                .Where(translation => translation.TenantId == tenantId)
                .ToArrayAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogDebug(exception, "Loading translations for tenant {TenantId} failed.", tenantId);
            throw;
        }
    }

    public async Task<int> ReadSomeAndAbandonAsync(string tenantId, int rows, CancellationToken cancellationToken)
    {
        // The enumerator is deliberately never disposed. Its data reader stays open on the connection until the
        // DbContext is disposed, which then has to close (consume) the reader as part of its own disposal.
        var enumerator = context.Translations
            .AsNoTracking()
            .Where(translation => translation.TenantId == tenantId)
            .AsAsyncEnumerable()
            .GetAsyncEnumerator(cancellationToken);

        var read = 0;
        while (read < rows && await enumerator.MoveNextAsync())
        {
            read++;
        }

        return read;
    }
}
