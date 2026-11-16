using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Internal;

#pragma warning disable EF1001 // DbContextPool and IDbContextPoolable are EF internals; this harness hooks them on purpose.

namespace Repro.Data;

/// <summary>
/// EF's own pool, plus two hooks for the dispose gate in <see cref="TranslationsContext"/>:
/// <list type="bullet">
/// <item><see cref="Rent"/> re-arms the gate, because one pooled instance serves many leases.</item>
/// <item>
/// The pool itself disposes instances it no longer keeps (on overflow in <c>Return</c>, and when the pool is disposed).
/// Those calls run with <see cref="IsPoolTeardown"/> set, so the gate does not report them as double disposals.
/// </item>
/// </list>
/// </summary>
public sealed class TrackingDbContextPool(
    DbContextOptions<TranslationsContext> options,
    IServiceProvider serviceProvider,
    DisposalDiagnostics diagnostics)
    : DbContextPool<TranslationsContext>(options, serviceProvider)
{
    private static readonly AsyncLocal<bool> s_poolTeardown = new();

    public static bool IsPoolTeardown => s_poolTeardown.Value;

    public override IDbContextPoolable Rent()
    {
        var context = base.Rent();
        ((TranslationsContext)context).OnRented();
        diagnostics.LeaseRented();
        return context;
    }

    public override void Return(IDbContextPoolable context)
    {
        s_poolTeardown.Value = true;
        try
        {
            base.Return(context);
        }
        finally
        {
            s_poolTeardown.Value = false;
        }
    }

    public override async ValueTask ReturnAsync(IDbContextPoolable context, CancellationToken cancellationToken = default)
    {
        s_poolTeardown.Value = true;
        await base.ReturnAsync(context, cancellationToken);
    }

    public override void Dispose()
    {
        s_poolTeardown.Value = true;
        try
        {
            base.Dispose();
        }
        finally
        {
            s_poolTeardown.Value = false;
        }
    }

    public override async ValueTask DisposeAsync()
    {
        s_poolTeardown.Value = true;
        await base.DisposeAsync();
    }
}
