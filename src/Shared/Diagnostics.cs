using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Repro;

public enum Phase
{
    /// <summary>The scenario's trigger and the bystanders run together.</summary>
    Trigger,

    /// <summary>The trigger has stopped; abandoned trigger work is given time to end.</summary>
    Draining,

    /// <summary>Only the bystanders run. Failures here mean the damage outlives the misuse.</summary>
    Quiet,
}

/// <summary>Counters and failure logging shared by the scenario trigger, the bystanders and the context.</summary>
public sealed class Diagnostics(ILogger<Diagnostics> logger)
{
    // Per category, how many events are logged in full before they are only counted.
    private const int DetailedLogsPerCategory = 3;

    private readonly ConcurrentDictionary<string, int> _logBudgets = new();
    private readonly ConcurrentDictionary<string, int> _triggerFailures = new();
    private readonly ConcurrentDictionary<string, int> _bystanderFailures = new();
    private readonly int[] _bystanderQueries = new int[3];
    private readonly int[] _bystanderFailed = new int[3];

    private int _phase;
    private int _iterations;
    private int _hung;
    private int _concurrentDoubleDisposals;
    private int _sequentialDoubleDisposals;
    private int _disposedInUse;
    private int _stolenLeases;
    private int _issueStackLogged;

    public Phase Phase
    {
        get => (Phase)Volatile.Read(ref _phase);
        set => Volatile.Write(ref _phase, (int)value);
    }

    public int BystanderFailures => _bystanderFailed.Sum();

    public int StolenLeases => Volatile.Read(ref _stolenLeases);

    /// <summary>True while the category still has budget for a full log entry.</summary>
    public bool TryLog(string category)
        => _logBudgets.AddOrUpdate(category, 1, (_, n) => n + 1) <= DetailedLogsPerCategory;

    public void Iteration() => Interlocked.Increment(ref _iterations);

    public void Hung() => Interlocked.Increment(ref _hung);

    public void DisposedInUse() => Interlocked.Increment(ref _disposedInUse);

    public void DoubleDisposal(bool concurrent)
        => Interlocked.Increment(ref concurrent ? ref _concurrentDoubleDisposals : ref _sequentialDoubleDisposals);

    public void StolenLease(long contextId, int expected, int actual)
    {
        Interlocked.Increment(ref _stolenLeases);
        if (TryLog("stolen-lease"))
        {
            logger.LogError(
                "STOLEN LEASE: ReproContext #{Id} was rented as lease {Expected} by this scope but is now on lease {Actual} " +
                "before the scope ended. Another scope is using the same instance.",
                contextId, expected, actual);
        }
    }

    /// <summary>A failure in the scenario's own (misusing) code.</summary>
    public void TriggerFailure(string where, Exception exception)
    {
        _triggerFailures.AddOrUpdate($"{where,-8} {Describe(exception)}", 1, (_, n) => n + 1);
        LogIssueStack(exception);
        if (TryLog($"trigger:{exception.GetType().Name}"))
        {
            logger.LogError(exception, "TRIGGER FAILURE ({Where}): {Type}", where, exception.GetType().Name);
        }
    }

    public void BystanderQuery(Phase phase) => Interlocked.Increment(ref _bystanderQueries[(int)phase]);

    /// <summary>A failure in correct code that only shares the pools with the misuse.</summary>
    public void BystanderFailure(Phase phase, Exception exception)
    {
        Interlocked.Increment(ref _bystanderQueries[(int)phase]);
        Interlocked.Increment(ref _bystanderFailed[(int)phase]);
        _bystanderFailures.AddOrUpdate($"{phase,-8} {Describe(exception)}", 1, (_, n) => n + 1);
        LogIssueStack(exception);
        if (TryLog($"bystander:{exception.GetType().Name}"))
        {
            logger.LogError(exception, "BYSTANDER FAILURE ({Phase}): a valid context in its own scope failed with {Type}", phase, exception.GetType().Name);
        }
    }

    /// <summary>Correct code got rows back that are not the ones it asked for.</summary>
    public void BystanderWrongResult(Phase phase, string problem)
    {
        Interlocked.Increment(ref _bystanderQueries[(int)phase]);
        Interlocked.Increment(ref _bystanderFailed[(int)phase]);
        _bystanderFailures.AddOrUpdate($"{phase,-8} WRONG RESULT: {problem}", 1, (_, n) => n + 1);
        if (TryLog("bystander:wrong-result"))
        {
            logger.LogError("BYSTANDER WRONG RESULT ({Phase}): {Problem}", phase, problem);
        }
    }

    public void PrintSummary(int freeSlots, int maxPoolSize)
    {
        Console.WriteLine();
        Console.WriteLine("==================== summary ====================");
        Console.WriteLine($"trigger iterations: {_iterations}, hung: {_hung}");
        Console.WriteLine($"double disposals: {_concurrentDoubleDisposals} concurrent, {_sequentialDoubleDisposals} sequential");
        Console.WriteLine($"disposed while in use: {_disposedInUse}");
        Console.WriteLine($"stolen leases: {_stolenLeases}");

        Console.WriteLine();
        Console.WriteLine("trigger failures (the misuse itself):");
        PrintTally(_triggerFailures, 12);

        Console.WriteLine();
        Console.WriteLine("bystanders (valid contexts, each in its own scope, used correctly):");
        Console.WriteLine($"  {"phase",-8} {"queries",8} {"failures",8}");
        foreach (var phase in Enum.GetValues<Phase>())
        {
            Console.WriteLine($"  {phase,-8} {_bystanderQueries[(int)phase],8} {_bystanderFailed[(int)phase],8}");
        }

        PrintTally(_bystanderFailures, 12);

        Console.WriteLine();
        Console.WriteLine($"Npgsql pool after all work stopped: {freeSlots} of {maxPoolSize} slots free, {maxPoolSize - freeSlots} still checked out");
    }

    // Logs, once, the first exception that went through the issue's frame.
    private void LogIssueStack(Exception exception)
    {
        if (exception.ToString().Contains("NpgsqlDataReader.Consume") && Interlocked.Exchange(ref _issueStackLogged, 1) == 0)
        {
            logger.LogError(exception, "ISSUE STACK: first failure that went through NpgsqlDataReader.Consume");
        }
    }

    private static void PrintTally(ConcurrentDictionary<string, int> tally, int top)
    {
        if (tally.IsEmpty)
        {
            Console.WriteLine("  (none)");
            return;
        }

        foreach (var (key, count) in tally.OrderByDescending(p => p.Value).Take(top))
        {
            Console.WriteLine($"  {count,6}  {key}");
        }
    }

    private static string Describe(Exception exception)
    {
        var e = exception is AggregateException { InnerExceptions.Count: 1 } aggregate ? aggregate.InnerException! : exception;
        var message = e.Message.Split('\n')[0];
        if (message.Length > 90)
        {
            message = message[..90] + "…";
        }

        var inner = e.InnerException is { } i ? $" → {i.GetType().Name}" : "";
        return $"{e.GetType().Name}{inner}: {message}";
    }
}
