using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Zouriel.Workflows.Storage;

namespace Zouriel.Workflows.EntityFrameworkCore;

/// <summary>The head of an audit chain (one row per tenant key): the chain's own state, and the lock row that serialises audit writers so the chain never forks.</summary>
internal sealed class AuditHeadRecord
{
    public string TenantKey { get; set; } = "";
    public long Seq { get; set; }
    public string Hash { get; set; } = "";
}

/// <summary>One row per (definition, change event) that already started (or was considered by) a trigger.</summary>
internal sealed class TriggerClaimRecord
{
    public string DefinitionId { get; set; } = "";
    public string EventId { get; set; } = "";
    public DateTimeOffset ClaimedAt { get; set; }
}

/// <summary>How <see cref="WorkflowModelBuilderExtensions.AddWorkflows"/> maps the workflow tables.</summary>
public sealed class WorkflowModelOptions
{
    /// <summary>Database schema for the workflow tables (null = the default schema).</summary>
    public string? Schema { get; set; }
    /// <summary>Table name prefix. Default <c>wf_</c>.</summary>
    public string TablePrefix { get; set; } = "wf_";
    /// <summary>Store instants as ticks (needed on SQLite, which cannot compare DateTimeOffset values).</summary>
    public bool InstantsAsTicks { get; set; }
}

/// <summary>Adds the workflow tables to a DbContext model.</summary>
public static class WorkflowModelBuilderExtensions
{
    /// <summary>
    /// The audit table's non-null tenant column (<c>""</c> without tenancy). The unique index on it and
    /// <c>Seq</c> backs up the chain head lock: NULL tenants would not collide in a unique index.
    /// </summary>
    internal const string AuditTenantKey = "TenantKey";

    /// <summary>
    /// Adds the workflow tables to your DbContext, so workflow state, audit and outbox rows commit in
    /// the same transaction as your own data. Call from <c>OnModelCreating</c>, then add a migration.
    /// </summary>
    public static ModelBuilder AddWorkflows(this ModelBuilder mb, Action<WorkflowModelOptions>? configure = null)
    {
        var o = new WorkflowModelOptions();
        configure?.Invoke(o);
        string T(string name) => o.TablePrefix + name;

        void Common<TEntity>(EntityTypeBuilder<TEntity> e, string table) where TEntity : class
        {
            e.ToTable(T(table), o.Schema);
            foreach (var p in e.Metadata.GetProperties())
            {
                p.SetColumnName(Snake(p.Name));
                if (p.ClrType != typeof(DateTimeOffset) && p.ClrType != typeof(DateTimeOffset?)) continue;
                // Instants are stored in UTC (PostgreSQL timestamptz only accepts offset zero).
                if (o.InstantsAsTicks) p.SetValueConverter(p.ClrType == typeof(DateTimeOffset) ? Ticks : NullableTicks);
                else p.SetValueConverter(p.ClrType == typeof(DateTimeOffset) ? Utc : NullableUtc);
            }
        }

        mb.Entity<DefinitionRecord>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Key).IsUnique();
            e.Property(x => x.Id).HasMaxLength(64);
            e.Property(x => x.Key).HasMaxLength(64);
            e.Property(x => x.RowVersion).IsConcurrencyToken();
            Common(e, "definitions");
        });
        mb.Entity<DefinitionVersionRecord>(e =>
        {
            e.HasKey(x => new { x.DefinitionId, x.Version });
            Common(e, "definition_versions");
        });
        mb.Entity<TriggerRecord>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.Entity, x.Type });
            e.HasIndex(x => x.DefinitionId);
            Common(e, "triggers");
        });
        mb.Entity<TriggerClaimRecord>(e =>
        {
            e.HasKey(x => new { x.DefinitionId, x.EventId });
            Common(e, "trigger_claims");
        });
        mb.Entity<InstanceRecord>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.Entity, x.EntityId });
            e.HasIndex(x => new { x.DefinitionId, x.Status });
            e.HasIndex(x => x.ParentId);
            e.Property(x => x.RowVersion).IsConcurrencyToken();
            Common(e, "instances");
        });
        mb.Entity<StepRecord>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.InstanceId);
            Common(e, "steps");
        });
        mb.Entity<BookmarkRecord>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.DueAt);
            e.HasIndex(x => x.InstanceId);
            e.HasIndex(x => new { x.Kind, x.Entity, x.EntityId });
            Common(e, "bookmarks");
        });
        mb.Entity<TaskRecord>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.Status, x.DueAt });
            e.HasIndex(x => x.InstanceId);
            e.HasIndex(x => new { x.Entity, x.EntityId });
            e.Property(x => x.RowVersion).IsConcurrencyToken();
            Common(e, "tasks");
        });
        mb.Entity<OutboxRecord>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.ProcessedAt, x.CreatedAt });
            Common(e, "outbox");
        });
        mb.Entity<AuditRecord>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property<string>(AuditTenantKey).HasMaxLength(64).IsRequired();
            e.HasIndex(AuditTenantKey, nameof(AuditRecord.Seq)).IsUnique();
            e.HasIndex(x => new { x.Category, x.Seq });
            e.HasIndex(x => new { x.Entity, x.EntityId });
            e.HasIndex(x => x.InstanceId);
            e.HasIndex(x => new { x.Category, x.At });
            Common(e, "audit");
        });
        mb.Entity<AuditHeadRecord>(e =>
        {
            e.HasKey(x => x.TenantKey);
            Common(e, "audit_head");
        });
        mb.Entity<SettingsRecord>(e =>
        {
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasMaxLength(32);
            e.Property(x => x.Revision).IsConcurrencyToken();
            Common(e, "settings");
        });
        return mb;
    }

    private static readonly ValueConverter<DateTimeOffset, DateTimeOffset> Utc = new(v => v.ToUniversalTime(), v => v.ToUniversalTime());
    private static readonly ValueConverter<DateTimeOffset?, DateTimeOffset?> NullableUtc =
        new(v => v.HasValue ? v.Value.ToUniversalTime() : null, v => v.HasValue ? v.Value.ToUniversalTime() : null);
    private static readonly ValueConverter<DateTimeOffset, long> Ticks = new(v => v.UtcTicks, v => new DateTimeOffset(v, TimeSpan.Zero));
    private static readonly ValueConverter<DateTimeOffset?, long?> NullableTicks =
        new(v => v.HasValue ? v.Value.UtcTicks : null, v => v.HasValue ? new DateTimeOffset(v.Value, TimeSpan.Zero) : null);

    internal static string Snake(string name)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c) && i > 0) sb.Append('_');
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }
}
