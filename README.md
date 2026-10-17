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
