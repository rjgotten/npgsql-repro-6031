# Repro harness for npgsql/npgsql#6031

[npgsql/npgsql#6031](https://github.com/npgsql/npgsql/issues/6031) ("NpgsqlDataReader.Close is open to race
condition") reports pods running out of memory, with dumps full of `ObjectDisposedException`s. Their async state
machines point at `NpgsqlDataReader.Consume`, reached through `DbContext.DisposeAsync` → `NpgsqlDataReader.CloseAsync`
when a DI scope tears down. The maintainers' position is that none of these types are thread-safe, so something must be
disposing concurrently. They closed the issue because no repro existed.

This solution reproduces the setup from the issue and instruments the one place where concurrent disposal would show
up: the `DbContext` itself.

## What it does

- **`TranslationsContext`** ([src/Repro/Data/TranslationsContext.cs](src/Repro/Data/TranslationsContext.cs)) overrides
  `DisposeAsync()` and `Dispose()`. Both go through one `Interlocked.CompareExchange` gate. The first call wins.
  Every later call is a **double disposal** and is logged with both callers, both thread ids, the time between them,
  and both stack traces. It is classified as:
  - **Concurrent**: the first disposal was still running. This is the race the issue suspects.
  - **Sequential**: the first disposal had already finished.
- **Registration** matches the issue: `AddDbContext` + `UseNpgsql(...)` with `SetPostgresVersion(14, 13)` and
  `EnableRetryOnFailure(1, 0.5s, null)`, plus a scoped `SqlTranslationsRepository` that runs
  `.AsNoTracking().Where(t => t.TenantId == ...).ToArrayAsync(ct)`.
- **`ReproWorker`** runs `Concurrency` loops in parallel. Each iteration creates a DI scope, runs the query, and lets
  the scope dispose the context. Some queries get a token that cancels a few milliseconds in, so disposal can land
  while a reader is mid-flight.
- **Container** is switchable between Microsoft.Extensions.DependencyInjection and Autofac.
- **`SummaryReporter`** logs one line of counters every 10 seconds, at Warning level once any double disposal has
  been seen.

## Running

With Docker only:

```sh
docker compose up --build            # Postgres 14 (stock image) + the repro app, default settings
docker compose down -v               # stop and remove the database volume
```

Override any setting per run with `-e Repro__<Setting>=<value>`:

```sh
# Prove the detector works: one deliberate Concurrent + one Sequential double disposal at startup.
docker compose run --rm --build -e Repro__SelfTest=true -e Repro__Duration=00:00:20 repro

# Autofac, synchronous scope disposal, one minute.
docker compose run --rm -e Repro__Container=Autofac -e Repro__ScopeDisposal=Sync -e Repro__Duration=00:01:00 repro
```

Against the compose database from the host (port 5432; set `POSTGRES_PORT` to remap):

```sh
docker compose up -d db
dotnet run --project src/Repro -- --Repro:Container=Autofac --Repro:Duration=00:01:00
```

The exit code is `1` when the self-test fails, and `0` otherwise.

## Settings (`Repro:*`)

| Setting | Default | Meaning |
|---|---|---|
| `Container` | `MsDi` | `MsDi` or `Autofac`. Swap in a custom `IServiceProviderFactory` in `Program.cs` to test your own container. |
| `ContextRegistration` | `Plain` | `Plain` (`AddDbContext`), `Pool` (`AddDbContextPool`) or `PooledFactory` (`AddPooledDbContextFactory`, which also registers a scoped context created through the factory). See [Pooling](#pooling). |
| `AliasRegistration` | `false` | Also register the context as `ITranslationsContext` through a factory and resolve it under both types in every scope. The scope then tracks the one instance twice and disposes it twice. |
| `ExtraDisposeProbability` | `0` | Chance that app code disposes the injected context itself (as `await using` over a DI-owned context would), before the scope disposes it again. |
| `Concurrency` | `32` | Parallel scope/query loops. |
| `ScopeDisposal` | `Async` | `Async` (`await using CreateAsyncScope()`, so the container calls `DisposeAsync`) or `Sync` (`using CreateScope()`, so it calls `Dispose`). |
| `DoubleDisposeMode` | `PassThrough` | What a detected repeat disposal does next. `PassThrough`: call base disposal, exceptions propagate. `PassThroughSwallow`: call base disposal, and log, count and swallow any exception. `Skip`: don't call base disposal. |
| `CancelProbability` | `0.2` | Chance that a query's token cancels after a random delay. |
| `MaxCancelDelayMs` | `5` | Upper bound of that delay. |
| `AbandonReaderProbability` | `0` | Chance to read 3 rows and abandon the enumerator, which leaves the reader open until the context is disposed. Only effective with `RetryOnFailure=false`, because a retrying execution strategy makes EF buffer the full result set and close the reader immediately. |
| `RetryOnFailure` | `true` | Toggle `EnableRetryOnFailure(1, 0.5s, null)`. |
| `Interval` | `00:00:00` | Delay between iterations per loop. Use `00:05:00` for the issue's cadence. |
| `Duration` | unset | Stop after this long. Unset means run until stopped. |
| `SummaryInterval` | `00:00:10` | Counter log cadence. |
| `SeedRows` / `Tenants` | `5000` / `10` | Seed data, inserted once into an empty table. |
| `SelfTest` | `false` | Run the deliberate double-dispose check before starting the load. |
| `CaptureFirstDisposerStack` | `true` | Capture a stack trace on every first disposal so a later double disposal can show who went first. Turn off to reduce overhead; the second disposer's stack is always captured. |
| `MaxDetailedLogs` | `50` | Per category (double disposals, dispose exceptions, iteration errors), how many events are logged in full before they are only counted. |

Raise `Logging__LogLevel__Microsoft.EntityFrameworkCore` (or `Npgsql`) to `Debug` for more detail. EF logs its own
`ContextDisposed` event at Debug.

## Reading the output

A detection looks like this:

```text
warn: Repro.Data.TranslationsContext[0]
      DOUBLE DISPOSAL of TranslationsContext #1234 (Concurrent): DisposeAsync on thread 17 came 0.412 ms after
      DisposeAsync on thread 9. DoubleDisposeMode=PassThrough.
      --- second disposer ---
         at ...
      --- first disposer ---
         at ...
```

The summary line reports `doubleDisposals concurrent=… sequential=…`. Exceptions thrown from the base disposal are
counted under `disposeExceptions objectDisposed=… invalidOperation=… other=…`, which is where the issue's
`ObjectDisposedException` would show up.

The other counters:

| Counter | Meaning |
|---|---|
| `contexts` | `TranslationsContext` instances constructed. With pooling, this stays near `Concurrency`. |
| `leases` | Rents from the pool (pooled modes only). |
| `firstDisposals` | Disposal calls that won the gate. With pooling this equals `leases` when the scope disposes every pooled context exactly once. |
| `poolTeardowns` | Disposals issued by the pool itself (overflow, pool disposal). These are not gated or counted as doubles. |
| `stolenLeases` | A scope's context switched to a different lease before the scope ended: another owner rented the same instance while it was still in use. |

## Pooling

With pooling, one instance serves many leases. `TrackingDbContextPool`
([src/Repro/Data/TrackingDbContextPool.cs](src/Repro/Data/TrackingDbContextPool.cs)) subclasses EF's `DbContextPool<T>`
to re-arm the gate on every `Rent()`. It also marks the pool's own `Dispose` calls so they are not reported. Both pooled
registrations make the DI scope own the context (EF registers it as a scoped factory, which the scope captures). They
differ in what the context's own `Dispose` does:

- **`AddDbContextPool`**: the context holds a *non-standalone* lease, so `DbContext.Dispose[Async]` is a no-op. The
  context goes back to the pool when the scope disposes the separate `ScopedDbContextLease`. Disposing the context
  twice is harmless.
- **`AddPooledDbContextFactory`**: the scoped context is created by `factory.CreateDbContext()` with a *standalone*
  lease, so `DbContext.Dispose[Async]` itself returns the context to the pool. A second disposal is a no-op only until
  someone else rents that instance. After that, it resets the new owner's context and returns it to the pool while
  the new owner is still using it.

## Findings

20–30 s runs, 32 loops, with the issue's provider settings. "Double disposal" here means the scope disposes the context
twice (`AliasRegistration=true`) or app code disposes it before the scope does (`ExtraDisposeProbability=0.2`).

| Registration | Double disposal? | Result |
|---|---|---|
| `Plain`, `Pool`, `PooledFactory` (MS DI and Autofac, async and sync scopes) | no | 0 double disposals, 0 stolen leases, 0 errors. With pooling, `leases == firstDisposals`. |
| `Plain` | yes | Every second disposal detected as Sequential; EF treats it as a no-op. No errors. |
| `Pool` | yes | Every second disposal detected as Sequential; EF treats it as a no-op. No errors. |
| `PooledFactory` | yes, MS DI or Autofac | Stolen leases, Concurrent double disposals, and Npgsql protocol corruption. |

The `PooledFactory` corruption shows up as these errors:

- `Row's number of columns (30840) differs from the row description's (4)`
- `Can't close, connection is in state Connecting`
- `The read on this field has not consumed all of its bytes`
- `Internal Npgsql bug: unexpected value 0 of enum BackendMessageCode`
- Hundreds of `ObjectDisposedException`s

The errors thrown *during disposal* follow the call path in the issue:

```text
at Npgsql.NpgsqlDataReader.NextResult(...)
at Npgsql.NpgsqlDataReader.Consume(...)
at Npgsql.NpgsqlDataReader.Close(...)
at Npgsql.Internal.NpgsqlConnector.CloseOngoingOperations(...)
at Npgsql.NpgsqlConnection.CloseAsync(...)
at Microsoft.EntityFrameworkCore.Storage.RelationalConnection.ResetStateAsync(...)
at Microsoft.EntityFrameworkCore.DbContext...IResettableService.ResetStateAsync(...)
at Microsoft.EntityFrameworkCore.Internal.DbContextPool`1.ReturnAsync(...)
at Microsoft.EntityFrameworkCore.DbContext.DisposeAsync()
```

That is the *repeat* disposer: it returns the context to the pool, and resetting the context closes a connection
whose reader belongs to the instance's new owner.

## Scope limits

- The internal multi-tenant Autofac factory is not included. Plain `AutofacServiceProviderFactory` stands in for it.
- The gate re-arm on `Rent()` is not atomic against a stray disposal racing it. Such a disposal is still detected,
  but it can be attributed to the new lease. `stolenLeases` is the unambiguous signal.
