#pragma warning disable EF1001 // IDbContextPool<> is an EF internal; Pool/PooledFactory modes replace it with TrackingDbContextPool.

using Autofac.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Internal;
using Repro;
using Repro.Data;

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(console => console.TimestampFormat = "HH:mm:ss.fff ");

var reproSection = builder.Configuration.GetSection(ReproOptions.SectionName);
builder.Services.AddOptions<ReproOptions>().Bind(reproSection);
var reproOptions = reproSection.Get<ReproOptions>() ?? new ReproOptions();

if (reproOptions.Container == ContainerKind.Autofac)
{
    // To test a custom container setup (e.g. a multi-tenant IServiceProviderFactory), swap the factory here.
    builder.ConfigureContainer(new AutofacServiceProviderFactory());
}

var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException("ConnectionStrings:Default is not configured.");

// Provider options mirror the registration posted in npgsql/npgsql#6031.
void ConfigureContext(DbContextOptionsBuilder options) => options.UseNpgsql(connectionString, npgsql =>
{
    npgsql.SetPostgresVersion(14, 13);
    if (reproOptions.RetryOnFailure)
    {
        npgsql.EnableRetryOnFailure(1, TimeSpan.FromSeconds(0.5), null);
    }
});

switch (reproOptions.ContextRegistration)
{
    case ContextRegistrationKind.Plain:
        builder.Services.AddDbContext<TranslationsContext>(ConfigureContext);
        break;

    case ContextRegistrationKind.Pool:
        // Registered first; EF's TryAddSingleton for the pool then leaves it in place.
        builder.Services.AddSingleton<IDbContextPool<TranslationsContext>, TrackingDbContextPool>();
        builder.Services.AddDbContextPool<TranslationsContext>(ConfigureContext);
        break;

    case ContextRegistrationKind.PooledFactory:
        builder.Services.AddSingleton<IDbContextPool<TranslationsContext>, TrackingDbContextPool>();
        builder.Services.AddPooledDbContextFactory<TranslationsContext>(ConfigureContext);
        break;
}

if (reproOptions.AliasRegistration)
{
    builder.Services.AddScoped<ITranslationsContext>(sp => sp.GetRequiredService<TranslationsContext>());
}

builder.Services.AddScoped<ITranslationsRepository, SqlTranslationsRepository>();

builder.Services.AddSingleton<DisposalDiagnostics>();
builder.Services.AddHostedService<SummaryReporter>();
builder.Services.AddHostedService<ReproWorker>();

using var host = builder.Build();

var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Repro");
logger.LogInformation(
    "Container={Container} ContextRegistration={ContextRegistration} AliasRegistration={AliasRegistration} " +
    "ExtraDisposeProbability={ExtraDisposeProbability} " +
    "Concurrency={Concurrency} ScopeDisposal={ScopeDisposal} DoubleDisposeMode={Mode} " +
    "CancelProbability={CancelProbability} MaxCancelDelayMs={MaxCancelDelayMs} " +
    "AbandonReaderProbability={AbandonReaderProbability} RetryOnFailure={RetryOnFailure} Interval={Interval} " +
    "Duration={Duration} ServiceProvider={ServiceProvider}",
    reproOptions.Container,
    reproOptions.ContextRegistration,
    reproOptions.AliasRegistration,
    reproOptions.ExtraDisposeProbability,
    reproOptions.Concurrency,
    reproOptions.ScopeDisposal,
    reproOptions.DoubleDisposeMode,
    reproOptions.CancelProbability,
    reproOptions.MaxCancelDelayMs,
    reproOptions.AbandonReaderProbability,
    reproOptions.RetryOnFailure,
    reproOptions.Interval,
    reproOptions.Duration?.ToString() ?? "(until stopped)",
    host.Services.GetType().FullName);

await Seeder.InitializeAsync(host.Services, reproOptions, logger, CancellationToken.None);

if (reproOptions.SelfTest && !await SelfTest.RunAsync(host.Services, logger, CancellationToken.None))
{
    return 1;
}

await host.RunAsync();
return 0;
