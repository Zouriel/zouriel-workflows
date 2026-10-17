# Zouriel Workflows

Workflows that admins design without code and your app runs inside its own database: automations that
start when a record changes, lifecycles that own a status field, approvals and tasks for people, and a
tamper-evident audit log of everything.

- **`spec/`**: the definition format (`SPEC.md`, JSON Schemas in `schema/`), the REST API
  (`api/openapi.json`), and conformance fixtures every engine must pass (`fixtures/`).
- **`dotnet/`**: the .NET engine (the first one; Laravel and Go engines are planned on the same spec).
- The builder and runtime components live in the UI libraries: Angular
  [`@zouriel/ui/workflow`](https://github.com/Zouriel/angular-ui-library) and Flutter
  [`zouriel_ui`](https://github.com/Zouriel/flutter-ui-library). Try them on the real engine in the
  [workflow studio](https://zouriel.com/uilib/workflow-studio).

## What a workflow can do

- **Automations** start when a record is created or deleted, a field changes (from/to chosen values), a
  date comes up, a schedule fires, someone presses a button, or the app reports an event. They run a tree
  of steps: approvals (any/all/count/percent of assignees, deadlines, reminders, escalation), tasks with
  outcomes, waits (time, date, condition, event), branches, switches, parallel paths, loops over related
  records, go-back loops, record updates, variables, notifications, webhooks, your own actions and
  sub-workflows.
- **Lifecycles** own a status field: states, transitions with who may apply them, checks, forms and
  confirmations, automatic transitions on timers, steps on each move, on entering or leaving a state or
  when a record stays too long, and an optional reset. In enforce mode any other write to the field is
  refused.
- **Runs** are versioned (a run keeps the version it started on), retried with backoff, and stop as
  incidents an admin can retry, skip or cancel. Working calendars count deadlines in working hours.
- **Audit**: every start, step, decision and move is written to a hash-chained log you can verify,
  filter, export and purge by retention rules, with sensitive values masked.

## .NET

```cs
// Program.cs
builder.Services.AddDbContext<AppDb>((sp, o) => o.UseNpgsql(cs).AddWorkflowInterceptor(sp));
builder.Services.AddWorkflows(wf => wf
    .UseEntityFrameworkCore<AppDb>()
    .Entity<Application>("application", e => e
        .Title(a => a.Title)
        .Field(a => a.Status, f => f.LifecycleCapable())
        .Field(a => a.Amount, f => f.Currency().Watchable().Settable()))
    .UseIdentity<AppWorkflowIdentity>()
    .UseNotifications<AppNotificationSender>());
app.MapWorkflowApi("/api/workflows", o => o.EditPolicy = "workflows.edit");

// AppDb.OnModelCreating
modelBuilder.AddWorkflows();
```

| Package | What it does |
| --- | --- |
| `Zouriel.Workflows` | The engine: definitions, interpreter, triggers, tasks, lifecycles, audit, lint, test runs. |
| `Zouriel.Workflows.EntityFrameworkCore` | Storage in your DbContext, and the SaveChanges interceptor that sees record changes (transactional outbox). |
| `Zouriel.Workflows.AspNetCore` | `AddWorkflows`, the background worker and the REST API (`MapWorkflowApi`). |
| `Zouriel.Workflows.Testing` | An in-memory test host and the conformance runner for the shared fixtures. |

Targets .NET 8 and .NET 10; tested on SQLite and PostgreSQL.

#### Upgrading: database migration

This version changes the workflow tables. After upgrading, add a migration
(`dotnet ef migrations add WorkflowsUpgrade`) and apply it. It adds:

| Table | Change |
| --- | --- |
| `wf_bookmarks` | `attempts` (int, not null, default 0) and `error` (text, null): failed timer firings, backoff and parking. |
| `wf_outbox` | `record_key` (text, null), and an index on `(record_key, processed_at, created_at)`: one record's events are processed in the order they were written. Outbox ids are now 32-character time-ordered strings; old ids still work. |
| `wf_instances` | `live_key` (text, null), and a unique filtered index `IX_wf_instances_live_key` on it where it is not null. |

`live_key` is how the database itself refuses a second live lifecycle instance of one workflow for one
record (`record:DEFINITION:ENTITY:ID`, held until the instance is cancelled) and a second running
automation of a `perRecordSerial` or `skipIfRunning` workflow for one record (`serial:DEFINITION:ENTITY:ID`,
held until it finishes), even when two units race: the loser fails on the index and is retried. Rows
written before the upgrade have no key, so they are not protected until they finish; to protect live
lifecycle instances at once, fill their keys in the migration (cancel any duplicates first, or the
statement fails on the index):

```sql
UPDATE wf_instances SET live_key = 'record:' || definition_id || ':' || entity || ':' || entity_id
WHERE kind = 'record' AND status <> 'cancelled';
```

(SQL Server: use `+` for `||`.) The filter needs filtered (partial) indexes: PostgreSQL, SQL Server and
SQLite have them.

With `WorkflowEngineSettings.Tenant` set, the engine reads, verifies and purges only that tenant's
audit chain (`AuditQuery.Tenant`, the tenant argument of `IWorkflowSession.PurgeAuditAsync`), in the EF
and in-memory stores alike; apps sharing one audit table no longer see or break each other's entries.
Custom stores must filter by it.

### Run the sample

```sh
cd dotnet/samples/Zouriel.Workflows.Sample
dotnet run --urls http://localhost:5190                          # one database, sign in with X-Demo-User
dotnet run --urls http://localhost:5190 --Playground:Enabled=true # a private sandbox per visitor
```

### Test

```sh
cd dotnet && dotnet test
```

The core tests include every fixture in `spec/fixtures`; the EF tests run on SQLite and on PostgreSQL
through Testcontainers (Docker needed).

## Status

The spec, the .NET engine and the UI libraries are in use; NuGet packages are not published yet.
See `PLAN.md` for the design and its reasoning.

## License

MIT, see `LICENSE`.
