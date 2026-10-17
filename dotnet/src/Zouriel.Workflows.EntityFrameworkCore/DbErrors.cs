using Microsoft.EntityFrameworkCore;
using Zouriel.Workflows.Engine;
using Zouriel.Workflows.Storage;

namespace Zouriel.Workflows.EntityFrameworkCore;

/// <summary>
/// Recognises database errors that mean "another unit got there first" from the providers' own error
/// codes (read by reflection, so this package needs no provider references), never from message text.
/// </summary>
internal static class DbErrors
{
    /// <summary>The workflow tables' record types. Unique violations on these are races between units.</summary>
    private static readonly HashSet<Type> WorkflowTypes =
    [
        typeof(DefinitionRecord), typeof(DefinitionVersionRecord), typeof(TriggerRecord), typeof(TriggerClaimRecord), typeof(InstanceRecord),
        typeof(StepRecord), typeof(BookmarkRecord), typeof(TaskRecord), typeof(OutboxRecord), typeof(AuditRecord), typeof(AuditHeadRecord),
        typeof(SettingsRecord),
    ];

    /// <summary>Runs a database call, turning races into <see cref="WorkflowConcurrencyException"/> so the engine retries the unit.</summary>
    public static async Task<T> MapAsync<T>(Func<Task<T>> work)
    {
        try
        {
            return await work();
        }
        catch (Exception e) when (ToConcurrency(e) is { } mapped)
        {
            throw mapped;
        }
    }

    /// <inheritdoc cref="MapAsync{T}(Func{Task{T}})"/>
    public static Task MapAsync(Func<Task> work) => MapAsync<bool>(async () => { await work(); return true; });

    /// <summary>The concurrency exception an error stands for, or null when it is a real failure.</summary>
    public static WorkflowConcurrencyException? ToConcurrency(Exception e)
    {
        if (e is WorkflowException) return null;
        if (e is DbUpdateConcurrencyException) return new WorkflowConcurrencyException(e.Message);
        if (IsDeadlock(e)) return new WorkflowConcurrencyException($"The database chose this unit as a deadlock victim: {Innermost(e).Message}");
        if (e is DbUpdateException u && IsUniqueViolation(e) && u.Entries.Count > 0 && u.Entries.All(x => IsWorkflowType(x.Metadata.ClrType)))
            return new WorkflowConcurrencyException($"Another unit wrote the same workflow row: {Innermost(e).Message}");
        return null;
    }

    /// <summary>True for a unique or primary key violation (Postgres 23505, SQL Server 2601/2627, SQLite 1555/2067).</summary>
    public static bool IsUniqueViolation(Exception e) => Causes(e).Any(x =>
        SqlState(x) == "23505"
        || SqlServerNumber(x) is 2601 or 2627
        || (Int(x, "SqliteErrorCode") == 19 && Int(x, "SqliteExtendedErrorCode") is 1555 or 2067));

    /// <summary>True for a deadlock or serialization failure (Postgres 40P01/40001, SQL Server 1205).</summary>
    public static bool IsDeadlock(Exception e) => Causes(e).Any(x => SqlState(x) is "40P01" or "40001" || SqlServerNumber(x) == 1205);

    private static bool IsWorkflowType(Type t)
    {
        for (var x = t; x is not null; x = x.BaseType)
            if (WorkflowTypes.Contains(x)) return true;
        return false;
    }

    private static IEnumerable<Exception> Causes(Exception e)
    {
        for (var x = e; x is not null; x = x.InnerException) yield return x;
    }

    private static Exception Innermost(Exception e) => Causes(e).Last();

    private static string? SqlState(Exception e) =>
        e.GetType().GetProperty("SqlState")?.GetValue(e) as string;

    private static int? SqlServerNumber(Exception e) =>
        e.GetType().FullName is "Microsoft.Data.SqlClient.SqlException" or "System.Data.SqlClient.SqlException" ? Int(e, "Number") : null;

    private static int? Int(Exception e, string property) => e.GetType().GetProperty(property)?.GetValue(e) as int?;
}
