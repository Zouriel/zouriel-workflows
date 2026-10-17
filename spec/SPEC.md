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
| `trigger.comment` | The remarks given with the lifecycle move or reset that started the run (§6), or `null`. |
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
`Yes`/`No`, `currency`/`number` with the number unformatted, `date` (and `today`) as `20 Oct 2026`,
`dateTime` (and `now`, `trigger.at`, `instance.startedAt`) as `19 Oct 2026, 14:30 UTC` in the settings
time zone (named after it), `null` as empty, arrays joined with `, `). Message text is for people;
machine fields elsewhere stay ISO 8601. A plain string is accepted wherever a template is and means `[string]`.

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
| `stateEntered` | Lifecycle sugar: `{ lifecycle: <definition key>, states: [stateKey…] }`. Fires when the record's lifecycle enters one of those states (by transition, auto transition, forced state or reset). |
| `transitionApplied` | `{ lifecycle, transitions: [transitionKey…] }`. Fires after one of those transitions is applied. |
| `deleted` | When a record is deleted. `entity.*` reads the snapshot taken before deletion. |
| `dateReached` | `{ field: <date/dateTime field>, offset: <signed ISO duration>, at?: "HH:MM" }`. Fires once per record when `field + offset` arrives (date fields fire at `at`, default `09:00`, in the settings time zone). Changing the field reschedules; clearing it or deleting the record cancels; a time already in the past when computed does not fire. Publishing plans the trigger for every existing record, replacing what an earlier version planned. |
| `schedule` | `{ cron: "<5-field cron>" }` (settings time zone). With `forEach: { when?: condition, limit }` it starts one instance per matching record (at most `limit`, max 500); without it, one instance with no record (`entity.*` is `null`; `entity` must then be omitted). |
| `manual` | `{ label, form?: <form definition>, who?: assignees }`. Started by a person from a record (`POST /records/{entity}/{id}/manual/{key}`). `input.*` reads the form. |
| `signal` | `{ signal: <signal key> }`. Started by the app raising the signal; `trigger.data.*` reads its data. A signal raised for a record starts automations of that record's entity; a signal raised for no record starts automations that have no `entity` (with no record, so they never queue). It resumes `wait` steps the same way: only waits about that record, or only waits about no record. |

**Edge semantics.** A `fieldChanged` trigger fires on a change *into* a matching value. It does not
fire for records already holding the value when the workflow is first published, nor when a change
leaves the value unchanged (`draft → draft`). Changes committed before the *first* publish never
start it; publishing a later version does not drop changes still waiting to be processed. One
committed change event starts at most one instance per published trigger (engines enforce it with a
unique `(triggerId, eventId)`). A change that is rolled back never starts anything. Each start is its
own unit of work: an automation that fails to start is audited as `trigger.failed` and does not stop
the others the same event starts. The change and lifecycle events of one record are processed in the
order they were written: while an older event of the record is still pending (failed and backing off,
or parked for an admin), newer ones wait.

**Loop protection.** Every instance carries `depth` (0 when started by a person or schedule; parent
depth + 1 when started by a change the engine itself made). A lifecycle's child runs (§6) carry the
depth of the move that started them (a move a workflow step applied at depth *d* + 1 starts runs at
depth *d* + 1), so a lifecycle and an automation feeding each other count up like any other chain.
Changes made at depth ≥ `maxChainDepth` (settings, default 5) do not start instances; the engine writes
an audit entry `trigger.suppressed` instead. A `subWorkflow` step whose child would get depth ≥
`maxChainDepth` does not start it: the step fails with `subWorkflowDepth` (never retried), so the run
stops in `incident` with a message naming the limit. Lint refuses sub-workflow cycles across the
published workflows (`subWorkflowCycle`). A workflow's own writes never re-trigger the *same* definition unless
its trigger sets `"retrigger": true`; this covers transitions its `applyTransition` steps apply
(`stateEntered`/`transitionApplied` triggers) as well as field writes. Writes a reset makes to take
back what a workflow did (§6) start no automations at all (the change event carries `suppressTriggers`).

**Concurrency** (`settings.concurrency`): `perRecordSerial` (default) — a new instance of the same
definition for the same record waits in `queued` status until earlier ones finish; `parallel` — no
queueing; `skipIfRunning` — the new start is dropped (audited as `trigger.skipped`). Engines enforce
this in the store, not only by checking first: a running serial instance holds a unique key
(definition, record) until it finishes, as a lifecycle's live record instance does until it is
cancelled; a unit that would add a second one fails to commit and is retried, and then queues, skips
or finds the existing instance.

## 6. Lifecycles

A lifecycle definition (`kind: "lifecycle"`) owns one `lifecycle: true` enum field of its entity.

- **Applies to.** `appliesWhen?: condition` says which records the lifecycle is for, read from the
  record's own fields only (`entity.<field>`, never the controlled field itself; `appliesWhenField`).
  Several lifecycles may share an (entity, field) when it tells them apart: at most one without
  `appliesWhen` (the fallback for every other record); a second one without it is refused
  (`lifecycleConflict`), and two conditions that are not clearly apart are a `lifecyclesMayOverlap`
  warning. A record gets its lifecycle when it is created (or, for records that existed before
  publishing, the first time it is used): the lifecycles with a condition it meets are tried first, in
  publish order (first publish time, then definition id), then the fallback; a record that meets none
  has no lifecycle on that field (the field is then not owned for it). A create that leaves out the
  condition's field is decided on the values it has, so it usually gets the fallback. The record is
  then **pinned** to that lifecycle (its record instance): it keeps it even if the fields the condition
  reads change, so pick fields that are fixed when the record is created (a type, a kind). The pin
  decides everything about the field for that record: field ownership and enforce/observe, the
  transitions offered, observed writes. The record's values decide only while it has no lifecycle on
  the field yet: a new record by its first values, an existing record by its values before the write.
  Cancelling the record instance (an admin cancel, or deleting the record) unpins the record; it is
  selected again on next use. A record whose lifecycle is archived stays pinned to it: while archived,
  the field has no lifecycle for that record (not owned, no transitions offered, nothing selected
  instead), and restoring the lifecycle resumes the same instance. A record never gets a second
  lifecycle for a field it already has a live instance on. Naming a lifecycle explicitly (the `lifecycle`
  of a transition, reset or forced state, or an `applyTransition` step) must name the record's own
  lifecycle for that field (its pinned one, or the one selection would pick when it has none yet);
  anything else is refused with `wrongLifecycle` (409) and never starts a second instance
  (`applyTransition` with `ifNotAllowed: "skip"` skips instead).
  The change hook decides ownership while the app saves and never reads the store, so engines keep the
  pins of fields where selection depends on the record (fields shared by several lifecycles, archived
  ones included, or with a condition) in their ownership cache: loaded with it, and updated in-process
  when the engine creates or cancels a record instance. Another node's pins reach a node at its next
  ownership refresh (`OwnershipRefreshInterval`), so for that long a record pinned elsewhere may be
  judged by its values. "Today" in a condition is the settings' time zone's date, in the change hook too.

- **States** map 1:1 to field values by `key` (the enum value). Each has a `category`: exactly one
  `initial`; any number of `active` and `terminal`. A state may have `onEnter` steps, `onExit` steps
  and an `sla: { within: <duration>, calendar?, steps: [...] }` (run once when the record has been
  in the state longer than `within`).
- **Transitions** `{ id, key, label, from: [stateId…] | "*", to: stateId, who, when, input, steps,
  auto, confirm, tone, hidden, comment }`.
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
  - `hidden: true` keeps it off the record panel; people can never apply it, only automations and
    the system.
  - `comment`: the **remarks** people give when they apply it: `"none"` (not asked; remarks sent
    are ignored), `"optional"` or `"required"`. Absent: `optional` when `tone` is `danger`, otherwise
    `none`. The request carries them as `comment` (a string): they are trimmed, empty means none, and
    a person may give at most 2000 characters (`invalidInput`). When the setting is `required` a
    person must give them (`commentRequired`, 400); automatic moves, the system and workflow steps
    never need them (an `applyTransition` step can pass its own, §7). Remarks are kept in the
    `lifecycle.transitioned` audit data as `comment` (only when given) and read as `trigger.comment`
    by the transition's `steps`, the `onExit`/`onEnter` runs of that move and the
    `stateEntered`/`transitionApplied` automations it starts.
- **Applying a transition** (by a person, by `applyTransition`, or automatically) is one atomic
  operation: check the record is in a `from` state → check `who` and `hidden` (people only; the API
  only accepts a signed-in person, never an anonymous or system caller) → check the guard →
  validate the input → check the remarks (people only) → run the current state's `onExit` (synchronous part) → write the field →
  record the transition → run the transition's `steps`, then `onEnter` of the new state (skipped if
  those steps already moved the record on) → commit. Steps
  run until they first wait; the rest continues in the background. If any check fails nothing
  changes and the API returns the error code (`notInFromState`, `notAllowed`, `guardFailed`,
  `invalidInput`, `commentRequired`, `staleState`, `wrongLifecycle`). The record view lists each transition the
  viewer may apply as `{ key, label, to, toLabel, tone, confirm, form, comment }`, with `comment` the
  resolved remarks setting (never `null`).
- **Record instance.** A lifecycle has one long-lived instance per record (`status: active` until
  the record reaches a terminal state, then `completed`). It is created when the record is created
  (state = the record's value, or the initial state when empty, which also writes the field) or,
  for records that existed before publishing, lazily the first time something changes the record
  through the lifecycle (a transition, a forced state, an observed write) with the record's current
  value. Reading a record never creates anything: the record panel shows the state the instance would
  have. A value that matches no state puts the instance in `outOfSync`. A **new** record enters its
  state like any move: the state's `onEnter` runs (trigger `type: "created"`, `to`: the state key), its
  timers are planned, automatic `when` transitions are checked, and `stateEntered` automations for that
  state start (`trigger.from` is `null`). A record that existed before publishing is only adopted: its
  timers are planned, nothing runs.
- **Runs.** `onEnter`, `onExit`, `sla` and transition `steps` each execute as a *child run* of the
  record instance (`parentId` set), pinned to the same version. Runs started by `onEnter`/`sla` of a
  state are cancelled when the record leaves the state, unless the state sets `"keepRunsOnExit":
  true`; a run whose own step moves the record out is not cancelled (it finishes its steps).
  Transition runs are never cancelled by later transitions.
- **Field ownership.** `mode: "enforce"` (default): a committed write to the field that did not go
  through the engine is refused (the engine's change hook raises `fieldOwned` before commit) and
  audited (`lifecycle.writeRefused`). `mode: "observe"`: the write is accepted; if a transition from
  the old state to the new one exists it is recorded as applied by `system` (its guard, input and
  `who` are not checked, its steps run); otherwise the instance becomes `outOfSync` (`lifecycle.outOfSync`
  with the value): the state's runs are cancelled and its timers dropped, and the instance keeps the
  last state it agrees with. It is in sync again when the value returns to that state, when a write
  reaches a state a transition from it leads to (recorded as that transition), or when a state is
  forced. Both are audited.
- **Force state.** Admins with `workflows.intervene` may move a record to any state with a reason
  (`POST /records/{entity}/{id}/force-state`). No guards or inputs are checked; `onExit`/`onEnter`
  run unless `skipSteps: true`. Either way the record leaves its state: the old state's timers are
  dropped and its `onEnter`/`sla` runs are cancelled (unless `keepRunsOnExit`). Audited as `lifecycle.forced`.
- **Reset.** `reset?: { label?, to?, who, confirm?, input?: { form }, comment?, states?, runExitEnter?,
  cancelAutomations?, steps? }` lets people start a record's workflow over (`POST /records/{entity}/{id}/reset` with
  `{ input?, comment?, undo? }`). Without `reset` a lifecycle cannot be reset. `comment` is the
  remarks setting, as for transitions but defaulting to `optional`. `states` lists the state ids the
  reset is offered in; absent = every state. Editors may offer choices such as "from a state onwards"
  (that state and every state reachable from it without passing through the reset's `to`), but they
  store the resulting list: the engine only checks that the record's current state is in it. An
  unknown state id is a lint error (`unknownState`); an empty list a warning (`resetNeverOffered`).
  Applying it, in one atomic operation:
  check the caller is a signed-in person among `who` (empty = nobody; `notAllowed`) → refuse when the
  record's current state is not in `states` (`notAllowed`, "“Start over” isn't offered in “Draft”.")
  → refuse when the
  record is already in `to` (default: the initial state) and in sync with nothing running
  (`nothingToReset`) → validate the input form (`invalidInput`) → check the remarks (`commentRequired`
  when required and empty; trimmed, at most 2000 characters, ignored when `none`) → cancel the record instance's
  unfinished child runs, and the unfinished automation instances about the record unless
  `cancelAutomations: false` (their open tasks are cancelled with them) → drop the instance's timers
  → run the current state's `onExit` only when `runExitEnter: true` → write the field to `to` → audit
  `lifecycle.reset` (before/after state, data: label, input masked, `comment` when given) → run `steps`
  as a child run (list id `reset`, reason `reset`; `input.*` reads the answers, `trigger.type` is
  `reset`, `trigger.comment` the remarks) → run
  `to`'s `onEnter` only when `runExitEnter: true`, otherwise plan its timers. The steps can change
  the record and other records: `setFields` with `target: <relation path>` changes a related record,
  and `forEach` over a collection with `setFields` `target: "item"` changes each of its items.
  The record view offers `reset: { label, to, toLabel, confirm, form, comment }` (`comment`: the
  resolved remarks setting) to people among `who` when the record's state is in `states` and a reset
  would change something, and `null` otherwise. Like any move into a state, a reset fires
  `stateEntered` triggers.
- **Taking back what the workflow did.** These completed steps write an undo journal entry
  (`StepRecord.undo`): `setFields` (`fields`: the record, each field's value before and, as the gateway
  holds it after the write, after), `createRecord` (`created`: the record and the values it was created
  with, as stored), `action` (its input and output), `notify` (`message`, only when it went to at least
  one recipient), `webhook` (what it called) and an applied `applyTransition` (`move`: the record,
  lifecycle and transition). No other step writes one. Every entry carries a `seq`, monotonic per engine
  (clock milliseconds × 1000, raised past the last one handed out), so entries written in one unit of
  work, which share a start time, keep their order; entries are ordered by `seq` (a stable sort).
  `GET /records/{entity}/{id}/reset` (`?lifecycle=` picks the lifecycle; its reset's `who` decides, else
  `notAllowed`, also when the reset would change nothing) returns `{ reset, items, token }`: the requested
  lifecycle's reset option and the unsettled journal entries of the record instance's own runs (never the
  reset runs, reason `reset`) and of the automation instances about the record (sub-workflows about the
  record included), oldest first. Runs of another lifecycle's record instance are never listed, and
  neither is work a sub-workflow did on another record. An item is `{ id, kind, title, detail?, workflow,
  step, undoable, selected, warning? }` with `kind`: `created` (`created:{entity}:{id}`, delete a record
  the workflow created, still present; unticked with "Changed since the workflow created it; ticking it
  deletes it anyway." when it no longer holds the values the workflows gave it), `field`
  (`field:{entity}:{id}:{field}`, one per field of any record, back to its value before the first
  change; left out when it already has that value, when it is the lifecycle's own field, or when its
  record is a `created` item; sensitive values show as "(hidden)"), `action` (`action:{stepRecordId}`,
  undoable only when the app's handler implements undo), `message`, `webhook` and `move`
  (`move:{stepRecordId}`, another record moved by `applyTransition`: "Moves of other records aren't taken
  back."; a move of this record's own lifecycle is not listed), never undoable. An item is `selected` by
  default unless it cannot be undone or someone changed it since the workflow set or created it, which
  the `warning` says. `token` fingerprints the items and the values they compare (a sensitive value only
  through what its item already shows). The reset body takes `undo: [item id…]` and `token?`; inside the
  reset's atomic operation, after cancelling the runs and before writing the field, the engine builds the
  list again: a `token` that no longer matches, or an id that is no longer in the list, fails the whole
  reset (`resetChanged`, 409: open the list again); a chosen item that cannot be undone fails it with
  `notUndoable`. It then **settles** every journal entry the list was built from, ticked or not (the
  entry records the reset that settled it), so the next reset lists only what happens after this one,
  whatever the clock says and however long a run lasts. The chosen items are taken back: app actions
  newest first (the handler's undo gets an `idempotencyKey`, the step record id: the reset's unit of work
  may be retried, so an undo can be asked for twice), then fields restored (one write per record), then
  created records deleted. Writes that take things back start no automations (`created`/`fieldChanged`/
  `deleted` triggers ignore them, so nothing redoes what was undone); lifecycle bookkeeping, condition
  waits and date triggers still follow them. Items left unticked stay as they are; they do not change
  what the lifecycle does next. The audit entry's data lists the titles undone (`undone`).
- **Versions.** The record instance is pinned to the version it started on. Publishing a new version
  does not move existing records; `POST /instances/{id}/migrate` (or bulk by definition) moves them,
  mapping states by key (a `stateMap` overrides; unmapped states make the request fail). Migrating
  into a terminal state completes the instance. An automation instance can migrate only when every
  position its live tokens hold points at the same step id in the new version.

## 7. Steps

Every step: `{ id, type, title?, note?, disabled?, retry?, timeout?, onError? }`. A disabled step is
skipped (audited as skipped). Steps run in order; a run ends when its last step finishes.

`retry: { max: 0–10, backoff: "fixed"|"exponential", initial: <duration>, maxDelay?: <duration> }`
applies to steps that call out (`action`, `webhook`, `notify`, `subWorkflow`). Delay for attempt
*n* (1-based retries): fixed = `initial`; exponential = `initial × 2^(n-1)` capped at `maxDelay`.
`onError`: `"stop"` (default — the run waits in `incident` for an admin), `"continue"` (carry on;
outputs `error` holds the message), or `{ "steps": [...] }` (run these, then continue after the
step). `timeout` (a duration) fails the attempt if the call takes longer (it counts as a failed
attempt: retried, then `onError`). A failed `action` attempt undoes the record writes its handler
made before failing. Record changes a handler makes and leaves unsaved (on the app's own data context)
are saved right after it returns, still as the workflow's writes (its actor, depth + 1 and causation),
never as anonymous writes when the unit commits.

`notify` steps queue the message in the transactional outbox and complete; delivery happens after
commit and is retried by the outbox (backing off up to an hour, parked for an admin after 10
attempts). A step's `retry` and `onError` never apply to delivery, so lint warns about them on a
`notify` step (`notifyDelivery`). Webhook `path` templates URL-encode the values they insert, so a
value cannot add path segments, a query or another host; webhooks do not follow redirects.

| Type | Shape | Outputs |
|---|---|---|
| `branch` | `paths: [{ id, label, when, steps }]`, `otherwise?: { id, label, steps }`. The first path whose `when` holds runs; else `otherwise`; else nothing. | `path` (id of the path taken, or `null`) |
| `switch` | `on: <token path>`, `cases: [{ id, label, values: [...], steps }]`, `otherwise?`. Matches like `anyOf`. | `case` |
| `parallel` | `paths: [{ id, label, steps }]` (2–10), `join: { type: "all"|"any"|"count", count? }`. All paths start together; the step finishes when the join is met. With `any`/`count`, paths still running are cancelled. | `completed` (path ids) |
| `forEach` | `collection: <token path to a collection>`, `when?: condition on item.*`, `mode: "sequential"|"parallel"`, `concurrency?` (parallel, default 5), `max` (≤ 500), `steps`. Items are read when the step starts. | `count` |
| `approval` | `assignees`, `rule: { type: "any"|"all"|"count"|"percent", value? }`, `due` (required), `calendar?`, `reminders?: [duration…]`, `escalation?: { after, assignees, notify? }`, `form?`, `instructions?: template`, `allowDelegation?`, `requireComment?: "never"|"reject"|"always"`, `outcomes: { approved: [...], rejected: [...], timedOut: [...] }` | `outcome` (`approved`/`rejected`/`timedOut`), `decidedBy` (user ids), `comments`, `data` (form answers of the last decision) |
| `task` | Like `approval` but `outcomes: [{ id, key, label, tone?, steps }]` (1–8), `onTimeout: { steps }`, rule is always first decision wins. | `outcome` (key), `decidedBy`, `comment`, `data` |
| `wait` | `until`: `{ type: "duration", duration, calendar? }` · `{ type: "date", date: <operand>, offset?, at? }` · `{ type: "condition", when, timeout, onTimeout: { steps } }` · `{ type: "signal", signal, timeout?, onTimeout? }` | `timedOut` (bool), `data` (signal data) |
| `setFields` | `target?: "self"|"item"|<relation token path>` (`item`: the current item inside `forEach`), `values: [{ field, value: operand }]` (fields must be `settable`). One write; it is a committed change like any other (it can trigger other workflows, §5 loop rules). | — |
| `createRecord` | `entity: <registry entity>`, `values: [{ field, value: operand }]` (fields must be `settable`, `fieldNotSettable`; constants among an enum field's options, `unknownValue`; not a field a lifecycle controls, this definition's or a published one, `ownedField`: it starts at its initial state; every field the registry marks `required` set, `missingRequiredField`). At run time a field any published lifecycle controls is refused (`ownedField`), since lint only knows the lifecycles published when the workflow was. Needs a gateway that can create records: `notSupported` when it cannot (or cannot make that type), `unknownEntity` when it does not store it. One committed create, like `setFields`. | `id` |
| `setVariables` | `values: [{ variable: <variable id>, value: operand }]` | — |
| `applyTransition` | `target?: "self"|<relation path>`, `lifecycle: <definition key>`, `transition: <key>`, `input?: { <key>: operand }`, `comment?: template` (remarks recorded with the move and read as `trigger.comment`; the transition's `required` check does not apply to a workflow), `ifNotAllowed: "fail"|"skip"` | `applied` (bool) |
| `notify` | `channel`, `to: recipients`, `subject?: template`, `body: template`, `link?: "record"|template` | `sent` (count) |
| `webhook` | `connection?` (named in settings) or `url` (must match an allowed origin), `method`, `path?: template`, `headers?: {name: template}`, `body?: json-with-operands`, `expect?: [status…]` | `status`, `body` |
| `action` | `action: <registry key>`, `input: { <key>: operand }`. A handler that can also undo (`undo(input, output)`) makes the action undoable by a reset. | the action's declared outputs |
| `subWorkflow` | `workflow: <automation key>`, `target?`, `wait: bool`, `input?` | `instanceId`, `status`, `outputs` (the child's `vars`) |
| `goTo` | `target: <step id>`, `maxTimes` (1–50). Jumps back to an earlier step in the same list or an enclosing list on the current path (never into or out of a `parallel` path or `forEach`). When the count is reached the step is skipped. Inside a `forEach` the count is kept per item. | `count` |
| `end` | `result: "completed"|"failed"`, `reason?: template` | — |

**Remarks on decisions.** `requireComment` on an `approval` or a `task`: `never` (default; a comment
is always allowed), `always` (every decision needs one), or `reject`: a comment is needed for an
approval's `rejected` outcome, and for any task outcome whose `tone` is `danger` (an outcome keyed
`rejected` counts too). A missing or blank comment fails the decision with `commentRequired` (400).
The task view lists the outcomes with their `tone`, so clients can tell which ones need a comment. A
task with `requireComment: "reject"` and no `danger` (or `rejected`) outcome never asks; lint warns
(`noNegativeOutcome`).

**Assignee rules for `approval`.** Each assignee decides once. `any`: first approve → approved; all
reject → rejected. `all`: any reject → rejected; all approve → approved. `count`/`percent`: approved
once the approvals reach `value` (percent of assignees, rounded up); rejected once it can no longer
be reached. When `due` passes first → `timedOut` (open tasks are cancelled). Escalation adds its
assignees at `after` (counted from the step's start) without removing the original ones.

Assignees resolve when the step starts; later changes to roles do not change an open task, except
delegation and reassignment (audited). A task cannot be delegated to someone already on it or who
already decided; after delegation or reassignment the rule is checked again, so a task the remaining
decisions already settle completes at once.

## 8. Assignees and recipients

`assignees` / `who` / `to` are arrays of:
`{ "role": key }` · `{ "group": key }` · `{ "user": id }` · `{ "field": <token path of a user field> }`
· `{ "managerOf": "initiator"|"assignees"|<token path> }` · `{ "initiator": true }`.
`to` also accepts `{ "email": template }`. Duplicates are merged. An approval whose assignees
resolve to nobody fails the step (`noAssignees`). Who "the initiator" is (for `{ initiator: true }`
and `managerOf: "initiator"`):

- inside a run (steps, tasks, recipients, `instance.initiator`): the person who started that instance
  (who triggered the automation or applied the move that started the run; `null` for system starts);
- in the `who` of a transition, of the reset and of a manual start from a record: the record's
  initiator, kept on its lifecycle record instance. That is the person who created the record (the
  signed-in user behind the create), or, when the app or the system created it (or it existed before
  the lifecycle was published), the first person who applies a transition to it. A record with no
  initiator yet, or a manual start with no record, matches nobody. It is never the person asking,
  so `{ initiator: true }` does not let everyone through.

## 9. Durations and calendars

Durations are ISO 8601 (`P3D`, `PT4H`, `P1DT12H`, `P2W`, `P1M`; months/years add calendar months).
With `calendar: "working"` the duration counts only working time from settings (`calendar.days`,
`calendar.hours` `["09:00","17:00"]`, `calendar.holidays` `["2026-12-25", …]`, `timeZone`): days count
working days (landing at the same time of day, moved forward into working hours), hours count
working hours. With no calendar in settings, `working` behaves as `calendar`. A duration longer than
50 years is invalid (`badDuration`). A time beyond the calendar's range (after year 9999, e.g. a date
`9999-12-31` plus an offset) never arrives: a `dateReached` trigger plans nothing for it, a `wait` for
it waits without a timer; it is never an error.

## 10. Instances, runs and statuses

Instance statuses: `queued`, `running`, `waiting` (on a person, timer or signal), `incident`
(a step failed with `onError: stop`), `completed`, `failed`, `cancelled`, and for record instances
`active`/`outOfSync`. Admin operations: `cancel`, `retry` (re-run the failed step now), `skip`
(continue after the failed step, with optional outputs), `migrate` (§6). When parallel paths stop in
several incidents, `retry` and `skip` act on one of them (chosen by token or failed step id, else the
first); the instance stays `incident`, showing the next one's error, until none is left.

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
  `restored`, `rolledBack`, `deleted`, `permissionsChanged` (data: `version`, `changes`, `draftSkipped`,
  `recordsMoved`).
- `instance`: `started` (data: trigger), `queued`, `completed`, `failed`, `cancelled` (actor: who
  cancelled it, data: reason), `migrated` (before/after `version`; data: `stateMap`, or `reason:
  "permissions"` when a permissions change moved it), `retried`, `skipped`, `incident`, `timerFailed` (a timer
  could not be processed; data: purpose, error, attempt, parked). A failing timer is retried with backoff (1,
  2, 4… minutes, up to an hour); after 10 attempts it is parked for an admin and the instance shows it: an
  automation or run token stops in `incident` (retry, skip or cancel as usual); a lifecycle record
  instance's automatic-transition or SLA timer, or a task's reminder or escalation (the task stays open),
  sets the instance's `error` and writes `instance.incident`.
- `step`: `started`, `completed` (data: outputs, masked), `failed` (data: error, attempt),
  `retryScheduled`, `skipped`, `branchTaken` (data: path), `waiting`.
- `lifecycle`: `created`, `transitioned` (before/after state, data: transition, input masked, `comment`
  when remarks were given),
  `autoTransitioned`, `autoSkipped` (an automatic transition's time came but its guard failed or the
  record had left the state), `slaBreached` (data: state, within), `forced` (data: reason),
  `reset` (before/after state, data: label, input masked, `comment` when given, `undone`), `writeRefused`,
  `writeObserved`, `outOfSync`.
- `task`: `created`, `assigned`, `reminded`, `escalated`, `delegated`, `reassigned`, `decided`
  (data: outcome, comment, answers masked), `cancelled`, `timedOut`.
- `trigger`: `suppressed`, `skipped`, `paused`, `resumed`, `failed` (data: eventId, error).
- `settings`: `updated` (before/after).
- `audit`: `exported`, `purged` (data: `category`, `before`, `count`, `ranges`: the exact
  `[from, to]` runs of sequence numbers deleted — the checkpoint that keeps the chain verifiable).

The chain head (last sequence number and hash) is kept as its own state, so purging the newest
entries never restarts the chain. Verification checks each entry's hash, each link, strictly
increasing sequence numbers, and accepts a gap only when every missing number lies in a range a purge
recorded.

Masking: values of `sensitive` fields, `entity.field` entries of `settings.audit.maskedFields`, and
form answers listed there as `input.key`, are replaced by `"***"` wherever data leaves the engine:
audit `before`, `after` and `data` (including step outputs and the trigger of `instance.started`), run
and task views, and test runs. A value is masked under such a key anywhere in the data, and also when
it equals (or, inside text, contains) the current value of a sensitive field of the record concerned,
so values copied into variables or messages are hidden too. Fields that are not `readable` never
appear in test runs.

## 12. Global settings

`schema/settings.schema.json`: `{ version, timeZone, calendar, defaults: { retry, reminders,
concurrency, maxChainDepth, forEachMax }, people: { delegation: bool }, channels: { <key>: { enabled,
footer? } }, webhooks: { allowedOrigins: [...], connections: [{ key, label, baseUrl, auth:
"none"|"secret", secretName?, header? }] }, audit: { retentionDays: { <category>: days },
maskedFields: ["entity.field", …] }, paused: [{ entity }] }`. Secrets are never stored in settings; a
connection names a secret the host resolves.

Saving checks every field and refuses the whole save with `invalidSettings` (the problems listed) when
one is wrong: a known time zone; working days among `mon`…`sun`, each once; working hours exactly two
`HH:mm` times, the end after the start; holidays real dates (`YYYY-MM-DD`); retry `max` 0–10, `backoff`
`fixed`/`exponential`, durations valid; reminders valid durations; `concurrency` one of the three modes;
`maxChainDepth` 1–20; `forEachMax` 1–500; allowed origins `scheme://host[:port]` only; connections with
a unique key, an http(s) `baseUrl`, a known `auth`, a `secretName` when `auth` is `secret`, a valid
`header` name; `retentionDays` at least 1; masked fields shaped `entity.field`/`input.key`; paused
entries naming an entity. Settings stored before these checks existed are read leniently: a value the
engine cannot use (hours that are not times, a holiday that is not a date, an unknown day) is ignored
and the default used (09:00–17:00, Monday–Friday), and the engine logs a warning; it never stops the
engine from working or the settings from being fixed.

## 13. Versions and lint

- `status`: `draft` | `published` | `archived`. Publishing validates (all lint errors block; warnings
  don't), increments `version`, stores an immutable copy, re-indexes triggers, and records a diff.
  Archiving removes the definition's triggers (it starts nothing new; running instances finish on their
  version). Archiving a lifecycle also drops its records' automatic-transition and SLA timers; restoring
  plans them again counted from when each record entered its state (an SLA that already ran in that
  state is not planned again; timers already due fire at once). Restoring lints the published version against the other published workflows first, like a
  publish (`lintErrors`, e.g. a second fallback lifecycle published meanwhile: `lifecycleConflict`), then
  re-indexes its triggers with its original first-publish time.
- Lint problems (code, severity, path, message) are listed in `fixtures/lint`. Editors and engines
  produce the same codes for the same definition.
- **Test runs** (`POST /simulate`) report the definition's lint problems with the result. A lifecycle
  tested on a record that would not get it (pinned to another lifecycle for the field, not meeting
  `appliesWhen` with the test values on top, or another lifecycle comes first in publish order) still
  runs, and its problems carry a `lifecycleNotSelected` warning saying so.
- **Permissions.** `POST /definitions/{id}/permissions` with `{ changes: [{ node, who }], note? }`
  changes who may do parts of a published workflow and publishes straight away (publish permission).
  A `node` is a transition id (its `who`), `reset` (`lifecycle.reset.who`), `trigger` (a manual
  trigger's `who`) or an approval/task step id (its `assignees`). The new version is the current
  published version plus only these changes (linted as usual), so unpublished draft edits are neither
  published nor lost; the draft gets the same changes where its nodes still exist, and the nodes it no
  longer has are returned (`draftSkipped`). What an empty `who` means depends on the node: a
  transition or the reset, nobody by hand; a manual start (`trigger`), anyone who can see the record
  (as when the builder leaves it empty); an approval or task needs someone, so it is refused
  (`lintErrors`, `noAssignees`). A `who` field must be a person field of the record (lint). A change
  that alters nothing publishes nothing (`changed: false`). After publishing, a lifecycle's record
  instances (not cancelled) on the previous published version move to the new one (its states and
  transitions are that version's, so they map 1:1; their state, timers and runs stay as they are), and
  so do instances on older versions whose states and transitions are exactly the same; each is audited
  as `instance.migrated` with `reason: "permissions"`, so people whose permission was revoked stop
  acting on existing records at once (`recordsMoved`). Instances on an older version whose states or
  transitions differ stay where they are (moving them could leave them in a state the new version does
  not have); they are counted as `recordsKept` and need an explicit migration. Automation runs already in progress, and a record's
  child runs, keep their version: their open tasks keep their assignees. The response is the
  definition summary plus `changed`, `draftSkipped`, `recordsMoved` and `recordsKept`. Errors:
  `unknownNode`, `notPublished` (409), `lintErrors`. Audited as `definition.permissionsChanged` (data:
  the new `version`, `changes`, `draftSkipped`, `recordsMoved`, `recordsKept`).
