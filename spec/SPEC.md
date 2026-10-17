# Zouriel Workflows specification, version 1

This document is the contract every engine (.NET, Laravel, Go) and every editor (Angular, Flutter)
implements. The JSON Schemas in `schema/` define the shapes; this file defines the meaning. The
conformance fixtures in `fixtures/` are the tie-breaker: when this text and a fixture disagree, the
fixture wins and this text is fixed.

## 1. Identity and references

- Every state, transition, step, branch path, case, outcome and variable has an immutable `id`
  (generated, `[A-Za-z0-9_-]{1,64}`, unique within the definition). Editors never show ids.
- Many also have an editable `key` (`[a-z][a-zA-Z0-9_]*`). Keys are what people and APIs use
  (transition keys, state keys, outcome keys). Renaming a key in a draft is allowed; publishing a
  renamed state or transition key while instances use it is a lint *warning* (`renamedInUse`).
- Every reference inside a definition points at an `id`, except **token paths** (below), which point
  at registry keys and step ids.

## 2. The registry

The host app registers, in code, what workflows may touch. Engines serve it at `GET /schema`
(`schema/registry.schema.json`). Nothing outside the registry can be read, watched, written or called.

```jsonc
{
  "entities": [{
    "key": "application", "label": "Application", "pluralLabel": "Applications",
    "titleField": "title",
    "fields": [
      { "key": "status", "label": "Status", "type": "enum",
        "options": [{ "value": "draft", "label": "Draft" }, …],
        "watchable": true, "settable": false, "lifecycle": true },
      { "key": "amount", "label": "Amount", "type": "currency", "watchable": true, "settable": true },
      { "key": "applicantEmail", "label": "Applicant email", "type": "email", "sensitive": true },
      { "key": "reviewer", "label": "Reviewer", "type": "user", "settable": true },
      { "key": "programme", "label": "Programme", "type": "relation", "entity": "programme" },
      { "key": "documents", "label": "Documents", "type": "collection", "entity": "document" }
    ]
  }],
  "actions": [{ "key": "finance.createInvoice", "label": "Create invoice", "category": "Finance",
               "entity": "application",
               "inputs": [{ "key": "amount", "label": "Amount", "type": "currency", "required": true }],
               "outputs": [{ "key": "invoiceNo", "label": "Invoice number", "type": "text" }] }],
  "signals": [{ "key": "payment.received", "label": "Payment received", "entity": "application",
                "fields": [{ "key": "amount", "label": "Amount", "type": "currency" }] }],
  "channels": [{ "key": "email", "label": "Email" }, { "key": "inApp", "label": "In-app" }],
  "roles": [{ "key": "reviewer", "label": "Reviewer" }],
  "groups": [{ "key": "finance", "label": "Finance office" }]
}
```

Field types: `text`, `longText`, `email`, `phone`, `url`, `number`, `currency`, `boolean`, `date`
(`YYYY-MM-DD`), `dateTime` (ISO 8601 UTC), `enum` (with `options`), `user` (a user id), `relation`
(one related record of `entity`), `collection` (many related records of `entity`), `json`.

Flags: `watchable` (may be used in triggers and condition waits), `settable` (may be written by
`setFields`), `lifecycle` (an `enum` field a lifecycle may own), `sensitive` (masked in audit and
run views), `readable` (default `true`; `false` hides it from tokens).

## 3. Token paths and operands

A token path is a dot path read at the moment it is evaluated:

| Path | Meaning |
|---|---|
| `entity.<field>` | The current value of a field on the workflow's record (read fresh each time). |
| `entity.<relation>.<field>` | A field of a related record (any depth the registry allows; `null` when the relation is empty). |
| `trigger.field`, `trigger.from`, `trigger.to`, `trigger.actor`, `trigger.at` | What started the instance (§5). `trigger.data.<key>` for signal and manual-input data. |
| `input.<key>` | The answers of the form collected by the transition being applied, or the manual trigger's form. |
| `steps.<stepId>.<output>` | An output of an earlier step (§7 lists them per step type). |
| `item.<field>` | Inside `forEach`, the current item (a record of the collection's entity). `item.index` is the 0-based position. |
| `vars.<variableKey>` | A workflow variable. |
| `instance.id`, `instance.startedAt`, `instance.initiator` | The running instance. |
| `now`, `today` | The current instant (UTC ISO) / the current date in the settings time zone. |

An **operand** is `{ "const": any }`, `{ "ref": "<token path>" }`, `{ "today": <days offset> }` or
`{ "calc": <calc> }` (the form engine's calculation JSON, with token paths in its `ref` args).

A **template** is an array of parts: strings, or `{ "ref": "<token path>" }`. Rendering joins them;
a ref renders the value as text (`enum` as its option label, `user` as the user's name, `boolean` as
`Yes`/`No`, `currency`/`number` with the number unformatted, `null` as empty, arrays joined with
`, `). A plain string is accepted wherever a template is and means `[string]`.

Reading a path that the registry does not allow is a lint error (`unknownToken`); at run time it
yields `null`. `steps.<id>.…` is only valid when that step is **upstream**: it comes before the
reading step on every path from the start (inside the same branch/path, or before the enclosing
branch). Reading a step that may not have run is a lint error (`tokenNotUpstream`).

## 4. Conditions

Conditions reuse the form builder's condition JSON (`schema/definition.schema.json#/$defs/condition`):
a rule `{ ref, op, value?, value2? }` or a group `{ match: all|any|none, conditions: [...] }`. In
workflows `ref` is a token path. The operators and their semantics are exactly those of the form
engine (`fixtures/conditions/*.json`, shared with the form builder), minus the form-only operators
`visible`, `hidden`, `complete`, `incomplete`, `rowsGte`, `rowsLte`. An empty group holds. An
unfinished rule never matches (except the negative operators on a non-empty value, as in forms).

Values of type `user`/`relation` compare by id. `enum` compares by stored value.

## 5. Triggers (automations)

An automation has exactly one `trigger`. A trigger may carry `when` (an extra condition evaluated
against the record **after** the change). The record is the instance's `entity`.

| `type` | Fires |
|---|---|
| `created` | Once when a record of the entity is created and committed. `trigger.from` is `null`. |
| `fieldChanged` | When a committed change modifies `field`. Options: `to` (array of values; fires only when the new value is one of them), `from` (array; the old value must be one of them). Both empty = any change. |
| `stateEntered` | Lifecycle sugar: `{ lifecycle: <definition key>, states: [stateKey…] }`. Fires when the record's lifecycle enters one of those states (by transition, auto transition or forced state). |
| `transitionApplied` | `{ lifecycle, transitions: [transitionKey…] }`. Fires after one of those transitions is applied. |
| `deleted` | When a record is deleted. `entity.*` reads the snapshot taken before deletion. |
| `dateReached` | `{ field: <date/dateTime field>, offset: <signed ISO duration>, at?: "HH:MM" }`. Fires once per record when `field + offset` arrives (date fields fire at `at`, default `09:00`, in the settings time zone). Changing the field reschedules; clearing it cancels; a time already in the past when computed does not fire. |
| `schedule` | `{ cron: "<5-field cron>" }` (settings time zone). With `forEach: { when?: condition, limit }` it starts one instance per matching record (at most `limit`, max 500); without it, one instance with no record (`entity.*` is `null`; `entity` must then be omitted). |
| `manual` | `{ label, form?: <form definition>, who?: assignees }`. Started by a person from a record (`POST /records/{entity}/{id}/manual/{key}`). `input.*` reads the form. |
| `signal` | `{ signal: <signal key> }`. Started by the app raising the signal for a record; `trigger.data.*` reads its data. |

**Edge semantics.** A `fieldChanged` trigger fires on a change *into* a matching value. It does not
fire for records already holding the value when the workflow is published, nor when a change leaves
the value unchanged (`draft → draft`). One committed change event starts at most one instance per
published trigger (engines enforce it with a unique `(triggerId, eventId)`). A change that is rolled
back never starts anything.

**Loop protection.** Every instance carries `depth` (0 when started by a person or schedule; parent
depth + 1 when started by a change the engine itself made). Changes made at depth ≥ `maxChainDepth`
(settings, default 5) do not start instances; the engine writes an audit entry
`trigger.suppressed` instead. A workflow's own writes never re-trigger the *same* definition unless
its trigger sets `"retrigger": true`.

**Concurrency** (`settings.concurrency`): `perRecordSerial` (default) — a new instance of the same
definition for the same record waits in `queued` status until earlier ones finish; `parallel` — no
queueing; `skipIfRunning` — the new start is dropped (audited as `trigger.skipped`).

## 6. Lifecycles

A lifecycle definition (`kind: "lifecycle"`) owns one `lifecycle: true` enum field of its entity. At
most one lifecycle per (entity, field) may be published; publishing a second is refused
(`lifecycleConflict`).

- **States** map 1:1 to field values by `key` (the enum value). Each has a `category`: exactly one
  `initial`; any number of `active` and `terminal`. A state may have `onEnter` steps, `onExit` steps
  and an `sla: { within: <duration>, calendar?, steps: [...] }` (run once when the record has been
  in the state longer than `within`).
- **Transitions** `{ id, key, label, from: [stateId…] | "*", to: stateId, who, when, input, steps,
  auto, confirm, tone, hidden }`.
  - `from: "*"` means every non-terminal state other than `to`.
  - `who`: assignees allowed to apply it (§8). Empty or missing = nobody by hand (automatic/system
    only). Hosts may additionally require the `workflows.transition` permission.
  - `when`: a guard condition. When false the transition is not offered and cannot be applied.
  - `input`: `{ form: <form definition>, mode?: "create" }`. The answers are validated by the form
    engine rules (required, built-in checks, visibility) server-side; `input.*` reads them.
  - `steps`: steps run after the state changes (an automation-style run, §7).
  - `auto`: `{ after: <duration>, calendar? }` (applied by the system after the record has spent that
    long in a `from` state), or `{ when: <condition> }` (applied by the system when the condition
    becomes true while in a `from` state; checked on entering the state and after every committed
    change to the record). Auto transitions ignore `who` but respect `when` (guard).
  - `hidden: true` keeps it off the record panel (only automations/system apply it).
- **Applying a transition** (by a person, by `applyTransition`, or automatically) is one atomic
  operation: check the record is in a `from` state → check `who` (people only) → check the guard →
  validate the input → run the current state's `onExit` (synchronous part) → write the field →
  record the transition → run `onEnter` of the new state and the transition's `steps` → commit. Steps
  run until they first wait; the rest continues in the background. If any check fails nothing
  changes and the API returns the error code (`notInFromState`, `notAllowed`, `guardFailed`,
  `invalidInput`, `staleState`).
- **Record instance.** A lifecycle has one long-lived instance per record (`status: active` until
  the record reaches a terminal state, then `completed`). It is created when the record is created
  (state = the record's value, or the initial state when empty, which also writes the field) or,
  for records that existed before publishing, lazily on first access with the record's current
  value. A value that matches no state puts the instance in `outOfSync`.
- **Runs.** `onEnter`, `onExit`, `sla` and transition `steps` each execute as a *child run* of the
  record instance (`parentId` set), pinned to the same version. Runs started by `onEnter`/`sla` of a
  state are cancelled when the record leaves the state, unless the state sets `"keepRunsOnExit":
  true`. Transition runs are never cancelled by later transitions.
- **Field ownership.** `mode: "enforce"` (default): a committed write to the field that did not go
  through the engine is refused (the engine's change hook raises `fieldOwned` before commit) and
  audited (`lifecycle.writeRefused`). `mode: "observe"`: the write is accepted; if a transition from
  the old state to the new one exists it is recorded as applied by `system` (its guard, input and
  `who` are not checked, its steps run); otherwise the instance becomes `outOfSync` until a valid
  state is reached. Both are audited.
- **Force state.** Admins with `workflows.intervene` may move a record to any state with a reason
  (`POST /records/{entity}/{id}/force-state`). No guards or inputs are checked; `onExit`/`onEnter`
  run unless `skipSteps: true`. Audited as `lifecycle.forced`.
- **Versions.** The record instance is pinned to the version it started on. Publishing a new version
  does not move existing records; `POST /instances/{id}/migrate` (or bulk by definition) moves them,
  mapping states by key (a `stateMap` overrides; unmapped states make the request fail).

## 7. Steps

Every step: `{ id, type, title?, note?, disabled?, retry?, timeout?, onError? }`. A disabled step is
skipped (audited as skipped). Steps run in order; a run ends when its last step finishes.

`retry: { max: 0–10, backoff: "fixed"|"exponential", initial: <duration>, maxDelay?: <duration> }`
applies to steps that call out (`action`, `webhook`, `notify`, `subWorkflow`). Delay for attempt
*n* (1-based retries): fixed = `initial`; exponential = `initial × 2^(n-1)` capped at `maxDelay`.
`onError`: `"stop"` (default — the run waits in `incident` for an admin), `"continue"` (carry on;
outputs `error` holds the message), or `{ "steps": [...] }` (run these, then continue after the
step). `timeout` (a duration) fails the attempt if the call takes longer.

| Type | Shape | Outputs |
|---|---|---|
| `branch` | `paths: [{ id, label, when, steps }]`, `otherwise?: { id, label, steps }`. The first path whose `when` holds runs; else `otherwise`; else nothing. | `path` (id of the path taken, or `null`) |
| `switch` | `on: <token path>`, `cases: [{ id, label, values: [...], steps }]`, `otherwise?`. Matches like `anyOf`. | `case` |
| `parallel` | `paths: [{ id, label, steps }]` (2–10), `join: { type: "all"|"any"|"count", count? }`. All paths start together; the step finishes when the join is met. With `any`/`count`, paths still running are cancelled. | `completed` (path ids) |
| `forEach` | `collection: <token path to a collection>`, `when?: condition on item.*`, `mode: "sequential"|"parallel"`, `concurrency?` (parallel, default 5), `max` (≤ 500), `steps`. Items are read when the step starts. | `count` |
| `approval` | `assignees`, `rule: { type: "any"|"all"|"count"|"percent", value? }`, `due` (required), `calendar?`, `reminders?: [duration…]`, `escalation?: { after, assignees, notify? }`, `form?`, `instructions?: template`, `allowDelegation?`, `requireComment?: "never"|"reject"|"always"`, `outcomes: { approved: [...], rejected: [...], timedOut: [...] }` | `outcome` (`approved`/`rejected`/`timedOut`), `decidedBy` (user ids), `comments`, `data` (form answers of the last decision) |
| `task` | Like `approval` but `outcomes: [{ id, key, label, tone?, steps }]` (1–8), `onTimeout: { steps }`, rule is always first decision wins. | `outcome` (key), `decidedBy`, `comment`, `data` |
| `wait` | `until`: `{ type: "duration", duration, calendar? }` · `{ type: "date", date: <operand>, offset?, at? }` · `{ type: "condition", when, timeout, onTimeout: { steps } }` · `{ type: "signal", signal, timeout?, onTimeout? }` | `timedOut` (bool), `data` (signal data) |
| `setFields` | `target?: "self"|<relation token path>`, `values: [{ field, value: operand }]` (fields must be `settable`). One write; it is a committed change like any other (it can trigger other workflows, §5 loop rules). | — |
| `setVariables` | `values: [{ variable: <variable id>, value: operand }]` | — |
| `applyTransition` | `target?: "self"|<relation path>`, `lifecycle: <definition key>`, `transition: <key>`, `input?: { <key>: operand }`, `ifNotAllowed: "fail"|"skip"` | `applied` (bool) |
| `notify` | `channel`, `to: recipients`, `subject?: template`, `body: template`, `link?: "record"|template` | `sent` (count) |
| `webhook` | `connection?` (named in settings) or `url` (must match an allowed origin), `method`, `path?: template`, `headers?: {name: template}`, `body?: json-with-operands`, `expect?: [status…]` | `status`, `body` |
| `action` | `action: <registry key>`, `input: { <key>: operand }` | the action's declared outputs |
| `subWorkflow` | `workflow: <automation key>`, `target?`, `wait: bool`, `input?` | `instanceId`, `status`, `outputs` (the child's `vars`) |
| `goTo` | `target: <step id>`, `maxTimes` (1–50). Jumps back to an earlier step in the same list or an enclosing list on the current path (never into or out of a `parallel` path or `forEach`). When the count is reached the step is skipped. | `count` |
| `end` | `result: "completed"|"failed"`, `reason?: template` | — |

**Assignee rules for `approval`.** Each assignee decides once. `any`: first approve → approved; all
reject → rejected. `all`: any reject → rejected; all approve → approved. `count`/`percent`: approved
once the approvals reach `value` (percent of assignees, rounded up); rejected once it can no longer
be reached. When `due` passes first → `timedOut` (open tasks are cancelled). Escalation adds its
assignees at `after` (counted from the step's start) without removing the original ones.

Assignees resolve when the step starts; later changes to roles do not change an open task, except
delegation and reassignment (audited).

## 8. Assignees and recipients

`assignees` / `who` / `to` are arrays of:
`{ "role": key }` · `{ "group": key }` · `{ "user": id }` · `{ "field": <token path of a user field> }`
· `{ "managerOf": "initiator"|"assignees"|<token path> }` · `{ "initiator": true }`.
`to` also accepts `{ "email": template }`. Duplicates are merged. An approval whose assignees
resolve to nobody fails the step (`noAssignees`). Who "the initiator" is: the person who triggered
or applied (`null` for system starts).

## 9. Durations and calendars

Durations are ISO 8601 (`P3D`, `PT4H`, `P1DT12H`, `P2W`, `P1M`; months/years add calendar months).
With `calendar: "working"` the duration counts only working time from settings (`calendar.days`,
`calendar.hours` `["09:00","17:00"]`, `calendar.holidays` `["2026-12-25", …]`, `timeZone`): days count
working days (landing at the same time of day, moved forward into working hours), hours count
working hours. With no calendar in settings, `working` behaves as `calendar`.

## 10. Instances, runs and statuses

Instance statuses: `queued`, `running`, `waiting` (on a person, timer or signal), `incident`
(a step failed with `onError: stop`), `completed`, `failed`, `cancelled`, and for record instances
`active`/`outOfSync`. Admin operations: `cancel`, `retry` (re-run the failed step now), `skip`
(continue after the failed step, with optional outputs), `migrate` (§6).

## 11. Audit

Every engine writes the audit entries in `fixtures/engine/*/expected` for the same events, in the
same transaction as the change. Entry: `{ seq, at, tenant?, actor: { type: user|system|workflow, id?,
name? }, category, action, subject: { type, id, entity?, entityId? }, before?, after?, data?,
correlationId, causationId?, prevHash, hash }`.

Hash: `hash = sha256_hex(prevHash + "\n" + jcs(entry without prevHash and hash))` where `jcs` is the
JSON Canonicalization Scheme (RFC 8785) and the first entry's `prevHash` is 64 zeros. The chain is per
tenant (or global without tenancy). `fixtures/audit/` checks byte-exact hashes.

Categories and actions:

- `definition`: `created`, `draftSaved`, `published` (data: `version`, `note`, `diff`), `archived`,
  `restored`, `rolledBack`, `deleted`.
- `instance`: `started` (data: trigger), `queued`, `completed`, `failed`, `cancelled`, `migrated`,
  `retried`, `skipped`, `incident`.
- `step`: `started`, `completed` (data: outputs, masked), `failed` (data: error, attempt),
  `retryScheduled`, `skipped`, `branchTaken` (data: path), `waiting`.
- `lifecycle`: `created`, `transitioned` (before/after state, data: transition, input masked),
  `autoTransitioned`, `forced` (data: reason), `writeRefused`, `writeObserved`, `outOfSync`.
- `task`: `created`, `assigned`, `reminded`, `escalated`, `delegated`, `reassigned`, `decided`
  (data: outcome, comment, answers masked), `cancelled`, `timedOut`.
- `trigger`: `suppressed`, `skipped`, `paused`, `resumed`.
- `settings`: `updated` (before/after).
- `audit`: `exported`, `purged` (data: range, count — the checkpoint that keeps the chain verifiable).

Masking: values of `sensitive` fields, and form answers marked sensitive in settings, are replaced by
`"***"` in `before`, `after` and `data`.

## 12. Global settings

`schema/settings.schema.json`: `{ version, timeZone, calendar, defaults: { retry, taskDue,
reminders, concurrency, maxChainDepth, forEachMax }, people: { delegation: bool, escalationDefault },
channels: { <key>: { enabled, sender?, footer? } }, webhooks: { allowedOrigins: [...], connections:
[{ key, label, baseUrl, auth: "none"|"secret", secretName? }] }, audit: { retentionDays: { <category>:
days }, maskedFields: ["entity.field", …] }, paused: [{ entity }] }`. Secrets are never stored in
settings; a connection names a secret the host resolves.

## 13. Versions and lint

- `status`: `draft` | `published` | `archived`. Publishing validates (all lint errors block; warnings
  don't), increments `version`, stores an immutable copy, re-indexes triggers, and records a diff.
- Lint problems (code, severity, path, message) are listed in `fixtures/lint`. Editors and engines
  produce the same codes for the same definition.
