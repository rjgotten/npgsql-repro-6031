// Alias: AddPooledDbContextFactory plus an interface registration that forwards to the context.
//
// AddPooledDbContextFactory has no service/implementation overload like AddDbContext<TService, TImpl> and
// AddDbContextPool<TService, TImpl>. Mapping an interface onto the context therefore takes a forwarding
// registration that emulates those overloads (and is also what a decorator over the context ends up doing):
//
//     services.AddPooledDbContextFactory<ReproContext>(...);
//     services.AddScoped<IReproContext>(sp => sp.GetRequiredService<ReproContext>());
//
// Resolving IReproContext resolves ReproContext (tracked by the scope) and returns the same instance (tracked
// again), so the scope disposes it twice. The scoped ReproContext of a pooled factory holds a standalone lease, so
// its own Dispose returns it to the pool. The second disposal is a no-op only until another scope rents the
// instance; after that it returns the new owner's context to the pool mid-use, and two owners share one connection.
//
// Valid bystander contexts on the same pools show what that does to correct code.
//
// Run:   docker compose run --rm alias [--registration factory|pool] [--resolve alias|direct] [--seconds 20] [--quiet 20] [--loops 16] [--bystanders 8]
// Local: dotnet run --project src/Repro.Alias -- [same arguments]   (REPRO_CONNECTION, default: the compose database)
//   factory: AddPooledDbContextFactory (corrupts)      pool:   AddDbContextPool (absorbs the double disposal)
//   alias:   resolve IReproContext (disposed twice)    direct: resolve ReproContext (disposed once)

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Repro;

const int Rows = 2000;
const long Sum = Rows * (Rows + 1L) / 2;

var arguments = new Arguments(args);
var pooledFactory = arguments.Get("registration", "factory") != "pool";
var throughAlias = arguments.Get("resolve", "alias") != "direct";
var scenario = new Scenario(arguments, defaultTriggerSeconds: 20);

var services = Scenario.CreateServices();
if (pooledFactory)
{
    services.AddPooledDbContextFactory<ReproContext>(options => options.UseNpgsql(scenario.DataSource));
}
else
{
    services.AddDbContextPool<ReproContext>(options => options.UseNpgsql(scenario.DataSource));
}

services.AddScoped<IReproContext>(sp => sp.GetRequiredService<ReproContext>());

var provider = services.BuildServiceProvider(validateScopes: true);
var diagnostics = provider.GetRequiredService<Diagnostics>();

var exitCode = await scenario.RunAsync(
    provider,
    $"Alias ({(pooledFactory ? "AddPooledDbContextFactory" : "AddDbContextPool")}): resolving "
        + (throughAlias ? "IReproContext, so the scope disposes the context twice" : "ReproContext, so the scope disposes it once"),
    async _ =>
    {
        await using var scope = provider.CreateAsyncScope();
        IReproContext context = throughAlias
            ? scope.ServiceProvider.GetRequiredService<IReproContext>()
            : scope.ServiceProvider.GetRequiredService<ReproContext>();

        var instance = (ReproContext)context;
        var lease = instance.ContextId.Lease;

        var values = await context.Database
            .SqlQueryRaw<int>($"SELECT g AS \"Value\" FROM generate_series(1, {Rows}) g")
            .ToListAsync();

        if (values.Count != Rows || values.Sum(v => (long)v) != Sum)
        {
            throw new InvalidDataException($"read {values.Count} rows summing to {values.Sum(v => (long)v)}");
        }

        // The lease number only changes when the instance is returned and rented again. A change while this scope
        // still holds the context means another scope is using the same instance.
        if (instance.ContextId.Lease != lease)
        {
            diagnostics.StolenLease(instance.Id, lease, instance.ContextId.Lease);
        }
    });

// Exit without disposing the provider or data source: disposing damaged connections can block.
Environment.Exit(exitCode == 0 && diagnostics.StolenLeases == 0 ? 0 : 1);
