# Repro for npgsql/npgsql#6031

[npgsql/npgsql#6031](https://github.com/npgsql/npgsql/issues/6031) reported pods running out of memory, with heaps
full of `ObjectDisposedException`s whose state machines pointed at `NpgsqlDataReader.Consume`, reached through
`DbContext.DisposeAsync`. This repository reproduces that failure in two minimal scenarios.

**In short:** when two parties drive one Npgsql connection at once (for example a close racing an in-flight
read), the connection's protocol state is corrupted. Npgsql then fails unsafely:

- Damaged connections go back into the connection pool, where valid contexts in their own scopes pick them up and
  fail, or hang indefinitely in a query despite the command timeout and a cancelled token.
- A corrupted length field becomes a huge buffer allocation in `NpgsqlReadBuffer.AllocateOversize`, which throws
  `OutOfMemoryException` on the issue's own `Close → Consume → NextResult` path.
- Pool slots whose close throws are never returned.

How two parties end up on one connection is application-specific. The two scenarios are the two ways we found:

| Scenario | How the concurrency arises |
|---|---|
| [Race](src/Repro.Race/Program.cs) | A DI scope ends while fire-and-forget work it started on its DbContext is still reading. This is what happened in the application behind the issue. |
| [Alias](src/Repro.Alias/Program.cs) | `AddPooledDbContextFactory` plus an interface registration that forwards to the context. The scope disposes the context twice, and the second disposal returns another scope's context to the pool mid-use. |

## Running

Docker only; the database needs no schema or data. Run one scenario at a time:

```sh
docker compose run --rm race
docker compose run --rm alias
docker compose run --rm alias --registration pool     # control: AddDbContextPool absorbs the double disposal
docker compose run --rm alias --resolve direct        # control: the context is disposed once
docker compose down -v
```

Or against the compose database from the host (.NET 10 SDK):

```sh
docker compose up -d db
dotnet run --project src/Repro.Race
dotnet run --project src/Repro.Alias -- --registration pool
```

| Argument | Default | Meaning |
|---|---|---|
| `--registration` | `plain` (race), `factory` (alias) | Race: `plain` (`AddDbContext`) or `pool` (`AddDbContextPool`). Alias: `factory` (`AddPooledDbContextFactory`) or `pool` (`AddDbContextPool`). |
| `--resolve` | `alias` | Alias only: `alias` resolves the interface (disposed twice), `direct` resolves the context (disposed once). |
| `--seconds` | `5` (race), `20` (alias) | How long the trigger runs. |
| `--quiet` | `20` | How long the bystanders run on their own afterwards. |
| `--loops` | `16` | Parallel trigger loops. |
| `--bystanders` | `8` | Parallel bystander loops. |
| `--pool-size` | `300` | Npgsql `MaxPoolSize`, sized so leaked slots don't starve the bystanders. The compose database allows 400 connections. |

The exit code is 1 when a bystander failed, a lease was stolen, or pool slots leaked.

## What each run does

Both projects use stock Microsoft.Extensions.DependencyInjection and console logging, and share
[src/Shared](src/Shared):

- **[ReproContext](src/Shared/ReproContext.cs)** overrides `Dispose`/`DisposeAsync` and logs, with the disposer's
  stack, a disposal that arrives while an operation still runs on the context (`DISPOSED WHILE IN USE`), and a
  second disposal within one lease, found through an `Interlocked.CompareExchange` gate (`DOUBLE DISPOSAL`,
  concurrent or sequential). Disposal always proceeds into EF unchanged.
- **[Bystanders](src/Shared/Bystanders.cs)** are valid contexts: each loop creates its own scope, resolves its own
  context, awaits one query, checks every row and lets the scope dispose the context once. They share only the
  registration with the trigger, and so EF's context pool (where there is one) and Npgsql's connection pool.
- **[Scenario](src/Shared/Scenario.cs)** runs the trigger next to the bystanders, then lets abandoned trigger work
  drain, then runs the bystanders alone ("quiet"), then probes how many Npgsql pool slots are still checked out.
  Every wait is bounded, and blocked loops are reported with where they are stuck.

The first three of each kind of event are logged in full, with stacks; after that they are counted. The first
failure that goes through `NpgsqlDataReader.Consume` is logged as `ISSUE STACK`. A summary follows at the end.

## The two scenarios

### Race

```text
trigger: owner thread                            trigger: fire-and-forget work
---------------------                            ----------------------------
scope = CreateAsyncScope()
context = resolve ReproContext                   context.Database.SqlQueryRaw(...).ToListAsync()
                                                   reading rows from the connection
await scope.DisposeAsync()
  → DbContext.DisposeAsync
  → NpgsqlConnection.CloseAsync
  → NpgsqlDataReader.Close → Consume   ◄── same connection ──►   still reading
connection goes back to the pool, or stays checked out
```

With `--registration pool`, the scope also returns the context itself to EF's pool while the work still uses it.

### Alias

`AddPooledDbContextFactory` has no service/implementation overload like `AddDbContext<TService, TImpl>` or
`AddDbContextPool<TService, TImpl>`, so exposing the context through an interface takes a forwarding registration:

```csharp
services.AddPooledDbContextFactory<ReproContext>(options => options.UseNpgsql(dataSource));
services.AddScoped<IReproContext>(sp => sp.GetRequiredService<ReproContext>());
```

Resolving `IReproContext` resolves `ReproContext`, which the scope tracks, and returns the same instance, which the
scope tracks again. The scope disposes it twice. The scoped context of a pooled factory holds a standalone lease, so
its own `Dispose` returns it to the pool:

```text
scope 1                                         scope 2
-------                                         -------
resolve IReproContext → instance X, lease n
query, done
dispose (interface entry): X back to the pool
                                                rent X, lease n+1 (EF resets _disposed)
                                                query starts, reader open
dispose (context entry): X now holds lease n+1,
  so DbContext.DisposeAsync returns X to the
  pool → ResetStateAsync → NpgsqlConnection
  .CloseAsync → NpgsqlDataReader.Close → Consume ◄── still reading on the same connection
                                                a third scope may now rent X as well
```

Under `AddDbContextPool` the scoped context holds a non-standalone lease and its own `Dispose` does nothing (the
separate scoped lease object returns it), and under `AddDbContext` the second `Dispose` is guarded by `_disposed`.
Both absorb the same double disposal.

## Results

Short runs in Docker on one machine (EF Core 10.0.4, Npgsql 10.0.3, PostgreSQL 14).

| Run | Trigger iterations | Bystander queries (failures) during trigger | After trigger | Pool slots still checked out |
|---|---:|---:|---|---:|
| `alias` (10 s) | 39,113 | 548 (94) | all 8 bystanders blocked in their query | 77 of 300 |
| `alias --registration pool` (10 s) | 36,890 | 24,427 (0) | 55,904 queries, 0 failures | 0 |
| `alias --resolve direct` (10 s) | 40,416 | 26,227 (0) | 55,948 queries, 0 failures | 0 |
| `race` (5 s) | 457 | 29,581 (10) | 119,247 queries, 0 failures | 21 of 300 |
| `race --registration pool` (5 s) | 518 | 17,515 (41) | 149,727 queries, 0 failures | 15 of 300 |

A second `alias` run reproduced the same picture: 63 bystander failures in 528 queries, all 8 bystanders blocked,
70 pool slots leaked.

## Findings

### 1. Concurrent use of one connection damages it, and the pool hands it to valid code

Once two parties drive one connection, its protocol state is corrupted, and the connection goes back into
Npgsql's pool. Valid contexts, each in its own scope and used correctly, then pick it up. While the trigger ran,
they failed with:

- Protocol desync: `Internal Npgsql bug: unexpected value 0 of enum BackendMessageCode. Please file a bug.`,
  `Received backend message BindComplete while expecting ReadyForQueryMessage. Please file a bug.`,
  `Source array was not long enough`, `The read on this field has consumed all of its bytes`.
- Another party's query on their connection: `A command is already in progress: SELECT g AS "Value" ...`.
- A context that another scope is using (alias, and race with `--registration pool`):
  `A second operation was started on this context instance`, `Cannot access a disposed context instance`.

Several of these messages ask the user to file an Npgsql bug, although the cause is concurrent use upstream.

### 2. Valid contexts can hang indefinitely

In both `alias` runs, all 8 bystanders ended up blocked inside their query (`ToListAsync`). They were still blocked
15 s after their cancellation token was cancelled, with a 5 s command timeout in effect. Neither the timeout nor
the cancellation released them. One trigger loop blocked the same way in the first run. In the race runs no
bystander blocked, and all trigger work that hung for more than 2 s had ended by the end of the 15 s drain.

### 3. A corrupted length field becomes an `OutOfMemoryException` on the issue's path

When the stream is desynced, Npgsql reads a garbage message length and tries to allocate a buffer that size
(lengths around 2 billion bytes were seen in other errors, such as `len: 2021161080`). It happened in both race runs:

- `race`: 34 times in the trigger (18 in the work, 16 in the scope's disposal) and once in a bystander.
- `race --registration pool`: at least 3 times in the trigger and once in a bystander.

The stack is the issue's own `Close → Consume → NextResult` path:

```text
System.OutOfMemoryException
   at System.Buffers.SharedArrayPool`1.Rent(Int32 minimumLength)
   at Npgsql.Internal.NpgsqlReadBuffer..ctor(...)
   at Npgsql.Internal.NpgsqlReadBuffer.AllocateOversize(Int32 count)
   at Npgsql.Internal.NpgsqlConnector.ReadMessageLong(...)
   at Npgsql.NpgsqlDataReader.NextResult(...)
   at Npgsql.NpgsqlDataReader.Consume(...)
   at Npgsql.NpgsqlDataReader.Close(...)
   at Npgsql.Internal.NpgsqlConnector.CloseOngoingOperations(...)
   at Npgsql.NpgsqlConnection.CloseAsync(...)
```

In a container with a memory limit, an allocation attempt of that size may end the process instead of throwing.
The pods in the issue were being OOM-killed with `Consume` frames on the heap. That is consistent with this
finding, but not proven by it.

### 4. Leaked pool slots are never returned

When a close throws partway through, the connection's pool slot stays checked out. After all work stopped and a
full GC, 15 to 77 of 300 slots were still checked out, depending on the run. Nothing reclaims them, so an
application that keeps hitting this eventually exhausts its pool.

### 5. Damaged connections do not linger once the misuse stops

In the race runs, the bystanders ran clean once the trigger stopped: 0 failures in 119,247 and 149,727 queries. (In
the alias runs every bystander was already blocked by then.) The lasting effects are the blocked calls and the
leaked slots, not corrupted connections waiting in the pool.

### 6. Only `AddPooledDbContextFactory` turns a double disposal into shared use

A scope that disposes its context twice is harmless under `AddDbContextPool` and `AddDbContext` (the controls:
0 failures, 0 leaked slots). Under `AddPooledDbContextFactory` the scoped context holds a standalone lease, and the
second disposal returns another scope's context to the pool. Since that registration has no service/implementation
overload, exposing the context through an interface needs exactly the forwarding registration that causes this.

### 7. In the application behind the issue, it was the race

The application's own trigger was a cache whose first fill was started on a request's scope without being awaited.
When the request returned early, its scope disposed the context while the fill was still reading from it. That is
the race scenario. The dumps' `DbContext.DisposeAsync` frame fits: the request scope is disposed asynchronously,
while the cache's periodic refreshes used separate scopes disposed synchronously. Switching the repository to
`IDbContextFactory` with a method-local `using` removed the dependency on the request's context, which is why that
change worked around the problem.

### 8. Possible hardening

These are suggestions for discussion, not tested fixes.

- **Npgsql: detect concurrent use atomically.** Existing checks ("A command is already in progress", "Can't close,
  connection is in state Connecting") read the state and then act on it, so real concurrency gets past them. An
  atomic state transition on entry to the read and close paths would turn a desync into a clear "concurrent use of
  connection" exception. That is detection, not thread safety.
- **Npgsql: never return a connection that may still be in use.** When a close finds another operation active, or
  the stream is out of sync, break the connector instead of pooling it, and release its slot when the close fails.
- **Npgsql: treat an implausible message length as corruption.** Break the connector instead of allocating a
  buffer for it.
- **Npgsql: honor command timeout and cancellation on a desynced connection**, so a valid caller fails instead of
  hanging.
- **Npgsql: reword "please file a bug"** on desync errors to mention concurrent use of a connection as a likely
  cause.
- **EF Core: give `AddPooledDbContextFactory`'s scoped context a non-standalone lease**, as `AddDbContextPool`
  does, so a double disposal of a DI-resolved context is harmless.

### A note on rates

The alias failure rate depends on timing: a scope's two disposals are close together, and the instrumentation's
stack capture on every disposal widens that window. An uninstrumented version reproduced the same failures at a
lower rate (18 errors in about 141,000 iterations). The controls stay clean with the same instrumentation.
