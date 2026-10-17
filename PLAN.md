# Zouriel Workflows — plan

Status: written 2026-10-17 as the plan; the spec, the .NET engine and the UI are now built (see README.md). Kept for the reasoning behind the design.

A workflow system in four parts that share one JSON format:

1. **`workflow-spec`**: the definition format (JSON Schema), the HTTP API contract (OpenAPI), the
   entity-registry format, and conformance fixtures every engine must pass.
2. **UI (Angular `@zouriel/ui` + Flutter `zouriel_ui`)**: a `ui/workflow` entry point with the builder,
   global settings, run viewer, audit viewer, task inbox and entity action panel.
3. **.NET engine (first, the real target)**: NuGet packages that store, trigger, run and audit workflows
   inside the host app's own database.
4. **Laravel and Go engines (later)**: ports that pass the same fixtures and serve the same API, so the
   UI works against any of them unchanged.

Scope decided with the owner: .NET first for a new project; **lifecycle mode and full audit are in v1**;
comprehensive rather than minimal.

---

## 1. Research conclusions that shape the design

- **Editor style.** The no-code leaders (Power Automate's new designer, Zapier, HubSpot, ServiceNow,
  Airtable, Monday) moved from free-form node canvases to a **vertical list of step cards with "+"
  between steps and branches drawn as columns**. It needs no manual layout, can't express invalid graphs,
  and turns into a plain list on a phone. Free canvases (n8n, Node-RED, Make) suit developers and don't
  work on touch: n8n has no mobile editor and bpmn-js removed touch support in v17. The free canvas is
  only used where it fits: the **lifecycle state diagram**.
- **Lifecycle model.** Jira's model maps directly onto admin processes: states, one-way transitions,
  and per transition *conditions* (who may), *validators* (input checks) and *post-functions* (effects).
  We call these **Who can do this / Check before / Then do**. HEMS's engine has the same shape.
- **Triggers** fire when a record *enters* a matching value, not for records already in it (Airtable's
  rule). They fire only on **committed** changes, delivered through a **transactional outbox**, at least
  once, and deduplicated per change event.
- **Approvals** always need a rejection and a timeout path, or the flow can wait forever (ServiceNow).
  The builder makes the timeout path mandatory.
- **Versioning.** Published versions never change. Running instances stay pinned to the version they
  started on (Camunda, Temporal "Pinned"). Migrating an instance to another version is an explicit,
  audited admin action.
- **Engine.** Build a small engine of our own, using Elsa 3 as the reference design. Borrow:
  - trigger and bookmark ("pause point") concepts;
  - a deterministic trigger index;
  - one transaction per commit, covering state, bookmarks, steps, outbox and audit;
  - server-provided descriptors that drive the designer.

  Don't depend on Elsa: its JSON is tied to .NET type names and it runs C#/JS expressions, which rules it
  out as a cross-language spec. Temporal and Durable Task are built for code-defined workflows and need
  extra servers.
- **Expressions.** No scripting. Conditions reuse the **form-builder condition JSON**
  (`{match, conditions:[{ref, op, value}]}`), so forms and workflows share one rule language and the
  existing sentence editor. Values are read through structured token references, never `eval`.
- **Angular/Flutter graph libraries** (Foblex, ngx-vflow, node_flow, fl_nodes) don't match across the
  two platforms, and the Flutter ones are pre-1.0 with small user bases. We build our own on top of the
  form builder's store and drag code plus a shared canvas viewport.

---

## 2. Concepts

| Concept | Meaning |
|---|---|
| **Entity** | A model type the app registers (e.g. `application`). Code whitelists which fields are readable, watchable, settable, and which actions exist. Admins choose from that list only. |
| **Workflow definition** | One JSON document with `kind: "automation"` or `kind: "lifecycle"`. Drafts are edited; publishing creates an immutable version. |
| **Automation** | A trigger, followed by a tree of steps. |
| **Lifecycle** | Owns one status field of one entity. Defines the states, the transitions between them, and the steps each transition runs. At most one published lifecycle per entity+field. |
| **Instance** | One run of a definition version for one record. A lifecycle has one long-lived instance per record; an automation has one per trigger firing. |
| **Bookmark** | A durable pause point: waiting for a person (task), a time (timer), or a signal. |
| **Task** | A human step: an approval, or input collected with a form-builder form. |
| **Audit entry** | An append-only, hash-chained record of every definition, instance, task and admin event. |

### Lifecycle and field triggers together

- A lifecycle in **enforce** mode (the default) owns its field. The EF interceptor rejects changes to that
  field made outside `ApplyTransition` (`WorkflowFieldOwnedException`). **Observe** mode accepts outside
  writes, records an audit entry, and moves the instance if a matching transition exists. Otherwise it
  marks the instance *out of sync*.
- Automations can trigger on a lifecycle-owned field ("when application enters Approved"). The trigger
  picker shows the lifecycle's states and transitions instead of raw values.

---

## 3. Definition format (sketch)

```jsonc
{
  "specVersion": 1,
  "id": "wf_8c1…", "key": "application-review", "kind": "lifecycle",
  "title": "Application review", "revision": 3,
  "entity": "application",
  "lifecycle": {
    "field": "status", "mode": "enforce",
    "states": [
      { "id": "st_draft", "key": "draft", "label": "Draft", "category": "initial" },
      { "id": "st_sub",   "key": "submitted", "label": "Submitted", "category": "active", "sla": "P5D" },
      { "id": "st_appr",  "key": "approved", "label": "Approved", "category": "terminal", "tone": "success" }
    ],
    "transitions": [{
      "id": "tr_1", "key": "approve", "label": "Approve",
      "from": ["st_sub"], "to": "st_appr",            // "from": "*" = any non-terminal state
      "who": [{ "role": "reviewer" }, { "field": "assignedReviewerId" }],
      "when": { "match": "all", "conditions": [{ "ref": "entity.documentsComplete", "op": "isTrue" }] },
      "input": { "form": { /* form-builder definition: remark, file… */ } },
      "steps": [ /* same step tree as automations */ ],
      "auto": null                                     // or { "after": "P14D" } / { "when": {...} }
    }],
    "layout": { "st_draft": [0, 0], "st_sub": [240, 0] } // optional; auto-layout fills the rest
  }
}
```

```jsonc
{
  "kind": "automation", "entity": "application",
  "trigger": { "type": "fieldChanged", "field": "status", "from": ["submitted"], "to": ["approved"],
               "when": { /* extra condition */ } },
  "steps": [
    { "id": "s1", "type": "approval", "title": "Finance sign-off",
      "assignees": [{ "role": "finance" }], "rule": { "type": "any" },
      "due": "P3D", "reminders": ["P1D"], "escalateTo": [{ "managerOf": "assignee" }],
      "form": { /* optional form-builder definition */ },
      "outcomes": { "approved": [ /* steps */ ], "rejected": [ /* steps */ ], "timedOut": [ /* required */ ] } },
    { "id": "s2", "type": "notify", "channel": "email", "to": [{ "ref": "entity.applicant.email" }],
      "template": { "subject": [ "Approved: ", { "ref": "entity.title" } ], "body": [ /* … */ ] } }
  ],
  "settings": { "concurrency": "perRecordSerial", "onError": "notifyOwner" }
}
```

Rules:
- Every node has an immutable generated `id`. Keys and labels can be edited, and all references point
  at ids, as in the form builder.
- Token references (`{ref: "entity.x"}`, `{ref: "steps.s1.output.decidedBy"}`, `{ref: "trigger.from"}`)
  are structured, typed by the registry, and limited to the trigger plus steps earlier on the same path.
- Durations are ISO 8601. A duration is in **working time** if the global settings define a calendar
  and the step sets `"calendar": "working"`.

### Triggers (v1)

| Type | Fires when |
|---|---|
| `created` | A record is created (optional condition). |
| `fieldChanged` | A watched field changes: any change / to a value / from → to. Fires on entering the value only. |
| `stateEntered` / `transitionApplied` | Lifecycle sugar over `fieldChanged`. |
| `deleted` | A record is deleted (gets a snapshot of the record). |
| `dateReached` | Relative to a date field ("3 days before `deadline`"); the engine keeps the timers in step when the field changes. |
| `schedule` | Cron or interval, optionally for each record matching a condition (capped). |
| `manual` | A button on a record, with optional input form; permission-checked. |
| `signal` | An app-raised named event (`engine.SignalAsync("payment.received", key, data)`). |

### Steps (v1)

| Step | Notes |
|---|---|
| `branch` | If / else-if / else, using conditions. |
| `switch` | Multi-path on one value (e.g. each enum value). |
| `parallel` | Structured parallel block. Joins on `all`, `any` (cancels the rest) or `count n`. No arbitrary merges. |
| `forEach` | Over a related collection registered by the app (`application.documents`), sequential or parallel with a limit, max items capped. |
| `approval` | Assignees from roles, users, record fields, `managerOf`, or groups. Rule: any / all / count / percent. Due date, reminders, escalation, delegation. Optional form. Outcomes approved / rejected / timedOut, where the timeout path is required. |
| `task` | Like `approval` but with free outcomes defined by the admin (e.g. "Needs changes"). |
| `wait` | For a duration / until a date (field or token) / until a condition becomes true (with required timeout) / for a signal. |
| `setFields` | Only fields registered as settable; values are constants or tokens. |
| `applyTransition` | Moves a lifecycle (this record or a related one). |
| `notify` | Channels from global settings (email, in-app, SMS, Telegram…), sent through the host's `INotificationSender`. Templates are token chips. |
| `webhook` | Only allowed origins from global settings. Sends an idempotency key, retries, and maps the response into outputs. |
| `action` | An app-registered action with typed input and output. The inspector is generated from its input schema using form-builder fields. |
| `subWorkflow` | Calls another automation, either waiting for its result or fire-and-forget. |
| `goTo` | "Repeat from step X", limited by `maxTimes`; the only loop besides `forEach`. |
| `end` | Ends as completed or failed, with a reason. |

Every executable step has `retry {max, backoff, initial, max}`, `timeout`, and
`onError: stop | continue | path`. When retries run out, the engine raises an **incident** that an
admin can retry, skip (with an output value) or cancel. Each of these is audited.

---

## 4. Audit (v1, first-class)

What is recorded, append-only. Each entry stores `seq, at, tenant, actor {type user|system|workflow,
id, name}, category, action, subject (definition / instance / task / record), before, after, data,
correlationId, causationId, ip/userAgent (when a person acted), prevHash, hash`.

- **Definitions:** created, draft saved (debounced), published (with a structured diff against the previous
  version and a plain-English summary, reusing the form builder's `diffDefinitions` approach), archived,
  restored, trigger enabled or disabled.
- **Instances:** started (what triggered it: the change event and the old/new values), every step
  started, finished, failed or retried (inputs and outputs, with secrets masked), every lifecycle
  transition (from, to, actor, input form submission), branch decisions (which condition matched),
  timers fired, signals received, completed, failed, cancelled.
- **Tasks:** created, assigned, reminded, escalated, delegated, reassigned, decided (outcome, comment,
  form data), timed out.
- **Admin interventions:** retry, skip, cancel, migrate to another version, force state (lifecycle
  override, which needs a reason), settings changed.
- **Field ownership:** rejected outside writes (enforce mode) and accepted outside writes (observe mode).

How it is kept:
- Audit rows are written **in the same transaction** as the change they describe, so no change happens
  without its audit row and no audit row exists for a change that didn't happen.
- **Tamper evidence:** each entry stores the SHA-256 of `prevHash + canonical JSON(entry)`, chained per
  tenant. `VerifyAuditChainAsync(from, to)` reports the first broken link. The database user can be
  restricted to `INSERT`/`SELECT` on the audit table (a migration helper does this for Postgres).
- **Masking:** registry fields marked `sensitive` are stored as `"***"` (or a hash) in before/after
  values. The level is set per field in global settings.
- **Retention:** per category, set in global settings. A purge job writes a "purged range" checkpoint
  entry so the chain still verifies.
- **Querying:** filter by record, instance, definition, actor, category or date. Export to CSV or JSON
  (each export is itself audited).

UI: an audit timeline that can be shown for a record, an instance or a definition, with a diff view of
before/after and a "verify chain" button for admins.

---

## 5. UI: `ui/workflow` (Angular) and `areas/workflow.dart` (Flutter)

Same API, behaviour and look on both sides, with tests and showcase pages on both, per both repos'
`AGENTS.md`. Every icon goes through a `WF_ICONS` map with a path-only spec (see the hugeicons rule). Only
tokens from `tokens.css` are used. Density follows the existing design direction.

### Components

| Component | Purpose |
|---|---|
| `ui-workflow-builder` | The editor. Two-way `definition` model, plus `published`, `instanceCount`, `schema` (registry), `globalSettings`, `api`. Outputs `problems` and `changes`. Views: **Build · Versions · Test**. |
| `ui-workflow-global-settings` | App-level workflow settings (§5.3). |
| `ui-workflow-run` | One instance drawn over its definition: per-step status badges, the path not taken greyed out, a step timeline with inputs and outputs, incident actions. |
| `ui-workflow-audit` | Audit timeline and table with filters, diff viewer, export, chain verify. |
| `ui-workflow-tasks` | Task inbox: my tasks / my team's tasks, due and overdue, decide with form, delegate. |
| `ui-workflow-record-panel` | Drop-in panel for a record page: current state badge, the transitions available to *this user* (with their input forms), open tasks, history. This is what HEMS's workflow side panel does by hand today. |
| `ui-workflow-state-badge` | A small state chip using the state's tone. |

The data access contract is `UiWorkflowApi`: an interface with a default HTTP implementation for the
spec'd REST API, configured through `provideUiWorkflow({ baseUrl, fetch })` so the app adds its own login.

### 5.1 Automation editor

- A vertical step list. The trigger card is at the top, each step is a card, and a "+" row between
  cards opens the step picker (search plus categories). Branches render as columns on wide screens, and
  as collapsible titled sections ("If approved / Otherwise") on narrow ones.
- **Drag:**
  - drag a step type from the palette onto any "+" slot;
  - drag a step card by its grip to move it, including into or out of branches;
  - drag a whole branch to reorder it.

  This uses the form builder's `FbDnd` pattern: pointer events, 4 px mouse threshold, 320 ms long press
  with 8 px tolerance on touch, grips with `touch-action: none`, auto-scroll at edges, drop line, Escape
  cancels. On Flutter, gesture settings come from `MediaQuery.maybeGestureSettingsOf`, per the
  2026-10-14 lesson.
- **Keyboard and fallback:** every card has a menu with Move up / Move down / Move to…, Duplicate,
  Disable, Delete. The list is a `role="tree"` with arrow-key navigation, and each card has an ARIA label
  like "Step 3 of 7: Send email, in branch Approved".
- **Inspector:** a side panel on wide screens and a bottom sheet on narrow ones (the form builder's
  breakpoints). Per-step editors are built from form-builder field editors. The **token picker** offers
  typed tokens from the registry and from earlier step outputs, with "other types" shown, not hidden.
  Conditions use the existing `ui-fb-condition` sentence editor with a workflow token source.
- **Problems:** a badge on each step plus a problem list. Examples: a missing timeout path, a token
  referring to a step that isn't upstream, an unregistered field, an unreachable step, a trigger-loop
  risk (§7).
- Undo/redo uses the form builder's snapshot store: 200 steps, with a merge key for typing and one step
  per drag.

### 5.2 Lifecycle editor (the canvas)

- **Diagram view** on the new shared `ui-canvas-viewport`. It offers pan (drag the background, wheel),
  zoom (ctrl+wheel, pinch, buttons), fit to view and a minimap. It is extracted from `path-editor`'s
  private pan/zoom so both use it.
  - States are cards and transitions are orthogonal arrows with labels.
  - **Drag a state** to move it; the position is saved in `layout`, and snapping comes from `ui/canvas`
    `uiSnap`.
  - **Drag from a state's handle to another state** to create a transition. On touch, tap the handle
    then tap the target, as React Flow does.
  - "Tidy up" runs the auto-layout.
- **Auto-layout** is our own small layered algorithm: longest-path ranking, barycentre ordering,
  orthogonal routing. It lives in the spec as pure functions with fixtures, so Angular and Flutter
  produce **identical** layouts. Graphs here have tens of states, so no elkjs is needed (and Flutter can't
  use it anyway).
- **List view** shows states as sections, each listing its outgoing transitions. It is the editing view
  on phones; the diagram stays available there, read-only with pan and zoom.
- Selecting a transition opens the inspector with tabs **Who can do this / Check before / Ask for
  (form) / Then do**. *Then do* embeds the automation step list. *Ask for* embeds a compact form builder.
  Automatic transitions are set in the same panel: after a time in the state, or when a condition holds.

### 5.3 Global settings (`ui-workflow-global-settings`)

Stored by the host as JSON, served by the engine API, with its own `version` and audited changes.

- **Connection & registry:** the schema endpoint status. A read-only browser of the entities, fields,
  actions and signals that code registered, so admins see what they can use.
- **Watching:** a list of every active trigger grouped by entity and field. It flags conflicts (two
  automations on the same transition, a lifecycle in observe mode with outside writers) and lets an admin
  pause triggers per entity.
- **People:** where assignees come from (roles and groups from the app), the `managerOf` rule, delegation
  rules (out-of-office), and the escalation default.
- **Channels:** notification channels the app enabled, default sender, template snippets (header/footer).
- **Calendar:** working days, hours, time zone, holidays (used by working-time durations and SLAs).
- **Defaults:** retry policy, task due time, reminder schedule, concurrency mode, max `forEach` items,
  max chain depth.
- **Webhooks:** allowed origins, and named connections whose secrets stay on the server and are only
  referenced by name.
- **Audit:** retention per category, field masking levels, who can view and export.
- Deleting or renaming something that published workflows use is warned about and blocked, as the form
  builder's source `usage` check does.

### 5.4 Versions and test

- **Versions tab:** version list, diff of the draft against the published version (plain-English summary
  plus a structural diff), publish with a change note, roll back (which publishes a copy of an old
  version), running instance counts per version, and bulk "migrate instances" with a state/step mapping.
- **Test tab:** pick a real record (or enter values). It calls `POST /simulate`, where the engine runs the
  version with side effects stubbed (no sends, no writes) and returns the path taken, decisions, and what
  each step *would* have done. The result is drawn with `ui-workflow-run`. The interpreter exists only in
  the backends; the frontends only lint, describe and lay out, so there is no third interpreter to keep
  in step.

---

## 6. .NET packages

Location: `/home/zouriel/personal/zouriel-workflows/dotnet`. Multi-target `net8.0;net10.0`. Released to
GitHub Packages (NuGet) together with the spec version.

| Package | Contents |
|---|---|
| `Zouriel.Workflows` | Model (from the JSON Schema), validator, interpreter, condition evaluator (a port of the form engine's operators), template renderer, registry builder, `IWorkflowEngine`, background workers (outbox relay, timer and reminder poller, schedule runner) as `IHostedService`, fake-clock seams. |
| `Zouriel.Workflows.EntityFrameworkCore` | `modelBuilder.AddWorkflows()` adds the tables **into the host's own DbContext**, so outbox, audit and business data commit together. Also the `WorkflowSaveChangesInterceptor`, stores, and claim queries (`FOR UPDATE SKIP LOCKED` on Postgres, `UPDLOCK, READPAST` on SQL Server). |
| `Zouriel.Workflows.AspNetCore` | `app.MapWorkflowApi("/api/workflows")`: the spec'd REST API with authorization policies (`workflows.edit`, `.publish`, `.intervene`, `.audit.view`, `.audit.export`, `.settings`). |
| `Zouriel.Workflows.Testing` | In-memory store, fake clock, fake senders, the conformance fixture runner. |

### Registering entities (code whitelists, admins choose)

```csharp
builder.Services.AddWorkflows(wf => wf
    .UseEntityFrameworkCore<AppDbContext>()
    .Entity<Application>("application", e => e
        .Label("Application").Key(a => a.Id)
        .Field(a => a.Status, f => f.Enum<ApplicationStatus>().Watchable().LifecycleCapable())
        .Field(a => a.Amount, f => f.Currency().Readable().Settable())
        .Field(a => a.Applicant.Email, f => f.Email().Readable().Sensitive())
        .Field(a => a.Deadline, f => f.Date().Watchable())          // dateReached triggers
        .Collection(a => a.Documents, "documents")                   // forEach
        .Relation(a => a.AssignedReviewer, "assignedReviewer"))      // assignee fields
    .Action<CreateInvoice, CreateInvoiceInput, CreateInvoiceOutput>("finance.createInvoice")
    .Signal("payment.received", s => s.CorrelateBy("applicationId"))
    .UseNotifications<AppNotificationSender>()
    .UseIdentity<AppWorkflowIdentity>());   // users, roles, groups, managerOf, current user
```

`GET /api/workflows/schema` serves this registry (filtered by what the current admin may see) and the
step descriptors. The builder builds its pickers from it.

### Change detection

`WorkflowSaveChangesInterceptor.SavingChangesAsync`:
- For each tracked entry of a registered entity, compare `OriginalValue` with `CurrentValue` on watched
  fields.
- Check lifecycle-owned fields (reject or record, per mode).
- Write `EntityChanged` outbox rows (entity, id, field, from, to, actor, correlation id, event id) and
  audit rows into the **same** context.
- Never publish from `SavedChanges`.

The outbox relay matches events against the trigger index `(tenant, entity, field)` and starts
instances, with a unique `(triggerId, eventId)` constraint that makes starts exactly-once.

Documented gap: `ExecuteUpdate`/`ExecuteDelete`/raw SQL bypass the change tracker. Writes to an
enforce-mode field that way can't be blocked. `engine.RecordChangeAsync(...)` covers deliberate bulk
paths, and a CDC adapter is a later option.

### Tables

`wf_definitions` (id, key, kind, version, status, json, hash, published_by/at, note) ·
`wf_triggers` (definition version → entity/field/from/to/type index) · `wf_instances` (definition +
version, entity/id, status, current_state, variables json, row_version, correlation, cause) ·
`wf_steps` (instance, node id, attempt, status, input, output, error, timings) · `wf_bookmarks`
(kind task/timer/signal, hash, due_at, payload) · `wf_tasks` (assignees, rule, outcome, decided_by,
form data, due, escalation level) · `wf_incidents` · `wf_outbox` · `wf_inbox` (dedup) · `wf_audit`
(hash chain) · `wf_settings` (versioned JSON).

### Execution

- **Commit unit:** the interpreter loop runs nodes until something blocks, then commits the instance,
  steps, bookmarks, outbox and audit in **one transaction**. No event-sourcing replay is needed.
- **Concurrency:** optimistic `row_version` on instances, retried on conflict, plus a short lease per
  instance. Per-record serial mode (the default) queues a second automation start for the same record
  until the first one blocks or ends.
- **Timers:** stored as bookmarks with `due_at` and claimed by the poller. Nothing is held in memory, so
  timers survive restarts.
- **Idempotency:** each action call gets `instanceId:nodeId:attempt`, passed to handlers and as the
  `Idempotency-Key` header on webhooks.
- **Lifecycle API** (also used by the record panel):
  - `GetAvailableTransitionsAsync(entity, id, user)`: guards evaluated, input forms attached.
  - `ApplyTransitionAsync(entity, id, key, user, input)`: one transaction covering the guard, input
    validation (the form engine's rules, ported), the state change, the *then do* steps up to the first
    wait, and audit.
  - `ForceStateAsync(…, reason)`.

---

## 7. Safety rules

- Definitions reference registry keys only. Publishing rejects unknown entities, fields or actions and
  any setting that fails the step's input schema. Run time checks again.
- No scripts, reflection by name, SQL or eval anywhere. Webhooks only go to allowed origins, and secrets
  are referenced by name.
- **Trigger loops:** publish-time analysis flags a workflow that sets a field it (or another workflow)
  triggers on. At run time, `causationId` chains are capped (default depth 5), and a workflow changing its
  own trigger field doesn't re-trigger itself unless it opts in.
- Limits: definition size, steps per definition, `forEach` items, parallel width, instance run time,
  and how many instances a schedule may start.
- Authorization per API operation, per-field visibility in the schema endpoint, and tenant filter on
  every query when multi-tenancy is on.

---

## 8. The spec repo and conformance

Layout: `/home/zouriel/personal/zouriel-workflows/spec`.
- `schema/definition.schema.json`, `settings.schema.json`, `registry.schema.json` (JSON Schema
  2020-12).
- `api/openapi.json`: the REST API all engines serve (exported from the .NET sample; a test fails when the two drift).
- `fixtures/engine/*`: each is `definition.json`, `registry.json`, `events.json` (changes, transitions,
  task decisions, signals, clock ticks) and `expected.json` (instance status and state, steps in order,
  outputs, tasks, bookmarks, **audit entries**, outbox messages).
- `fixtures/lint/*`: invalid definitions and the problems each must produce. The TS and Dart linters run
  these too.
- `fixtures/layout/*`: lifecycle graphs and their expected coordinates, so Angular and Flutter lay out
  identically.
- `fixtures/conditions/*`: operator edge cases shared with the form engine (dates, empty values, numbers
  as strings).

As with the form builder, the fixtures are copied into the UI repos, and each runner checks the exact
fixture list.

---

## 9. Phases

Each phase ends with tests green in every affected repo, plus a showcase page or sample app that shows
it working.

| # | Phase | Delivers | Exit check |
|---|---|---|---|
| 0 | **Spec** | Definition, registry and settings schemas; OpenAPI draft; first 30–40 engine fixtures (lifecycle, triggers, approvals, waits, audit); lint, condition and layout fixtures. | Schemas validate all fixture definitions. The owner reviews the example definitions. |
| 1 | **.NET core** | Model, validator, condition evaluator, interpreter over in-memory store with fake clock; lifecycle API; audit writer with hash chain. | All engine fixtures pass in `Zouriel.Workflows.Testing`. |
| 2 | **.NET persistence & triggers** | EF Core mapping into the host context, interceptor + outbox, relay, timer, reminder and schedule workers, incidents, migrations for Postgres and SQL Server. | Integration tests on real Postgres and SQL Server (Testcontainers): rollback starts nothing, a crash after commit still starts, duplicates dedupe, timers survive restart, enforce mode blocks outside writes, audit chain verifies. |
| 3 | **.NET API & sample app** | `MapWorkflowApi`, authorization policies, `/simulate`, a sample ASP.NET app with 2–3 entities. | OpenAPI conformance test against the sample app; the full lifecycle is driven through HTTP only. |
| 4 | **UI foundations (both libs)** | `ui-canvas-viewport` extracted; `ui/workflow` entry point; TS and Dart model, linter, describer, layout; `UiWorkflowApi`. | Lint, layout and condition fixtures pass in both; the viewport has its own showcase page. |
| 5 | **Automation editor (both libs)** | Step list, drag/drop, inspector, token picker, step editors, problems, undo/redo, phone layout. | Showcase against the sample app. Drag tested with mouse, touch emulation and Flutter 8 dp slop. Playwright screenshots in every theme. |
| 6 | **Lifecycle editor (both libs)** | Diagram with drag/connect/tidy, list view, transition inspector with embedded form builder and step list. | Same as phase 5, plus layouts identical across the two libraries. |
| 7 | **Global settings, versions, test** | `ui-workflow-global-settings`, versions/diff/publish/migrate, Test tab with `/simulate`. | Settings round-trip through the API; migration shown end to end. |
| 8 | **Runtime UI** | `ui-workflow-run`, `ui-workflow-audit`, `ui-workflow-tasks`, `ui-workflow-record-panel`, `ui-workflow-state-badge`. | The sample app runs a real approval with timeout and escalation entirely through these components. |
| 9 | **Release** | NuGet packages, `@zouriel/ui` / `zouriel_ui` minor release, docs (README + docs site + Flutter showcase), adoption guide for the new project. | The new project installs released versions only. |
| later | Laravel package, Go package, CDC adapter, BPMN-ish import/export. | Same fixtures and API. |

Order: phases 1–3 (backend) and 4 (UI foundations) can run side by side once phase 0 is settled. The
editors (5–6) need the phase 3 sample app to test against real registry data.

---

## 10. Decisions still open (defaults in bold)

1. Database for the new project: **Postgres** (SQL Server also supported in phase 2).
2. Multi-tenancy: **built in as an optional tenant id**, off unless configured.
3. Where the code lives: **one repo `zouriel-workflows` with `spec/`, `dotnet/` (and later `laravel/`,
   `go/`)**; the UI stays in the two UI library repos.
4. Notification channels in v1: **email + in-app via the host's sender interface**; Telegram/SMS are
   the host's own implementations.
5. Identity source: **the host implements `IWorkflowIdentity`** (users, roles, groups, managerOf).

## 11. Main risks

- **Size.** This is several times the form builder. Mitigation: the phase gates above, and the engine
  fixtures written first so every phase has a fixed target.
- **Changing published definitions while instances run.** Mitigation: pinned versions, explicit
  migration with mapping, and `diff` warnings in the builder.
- **Trigger loops and storms.** Mitigation: §7 analysis, depth caps, per-entity pause switch in
  settings.
- **Writes that bypass the ORM.** Mitigation: documented gap, `RecordChangeAsync`, later CDC.
- **Drag on real phones.** Flutter tests don't behave like devices (2026-10-14 lesson). Nothing touch
  related is called done until it has been tried on a phone.
