# Conformance fixtures

These files are the shared definition of how a Zouriel Workflows engine behaves. Every engine
implementation (the .NET one today, others later) runs the same files and must pass all of them.
This page describes the format exactly as the reference runner implements it
(`dotnet/src/Zouriel.Workflows.Testing/ConformanceRunner.cs` and `WorkflowTestHost.cs`). When the
runner and this page disagree, the runner is right and this page is a bug.

| Folder | What it checks | .NET runner |
| --- | --- | --- |
| `engine/` | Whole scenarios: records change, workflows run, people decide, time passes. | `ConformanceRunner` via `EngineFixtureTests` |
| `lint/` | Each definition produces exactly the listed lint problems. | `LintFixtureTests` |
| `conditions/` | The rule language: conditions and calculations, case by case. | `ExpressionFixtureTests` |
| `audit/` | The audit hash chain, byte for byte. | `CoreUnitTests` |

All values are compared as workflow values: numbers are doubles (`1` equals `1.0`), instants are
UTC text `YYYY-MM-DDTHH:mm:ss.fffZ` (always three fraction digits), dates `YYYY-MM-DD`, times `HH:mm`,
enums their names, ids text. Text is compared exactly (case and spaces count).

## Engine fixtures (`engine/*.json`)

Files whose name starts with `_` are not fixtures: they are shared files other fixtures point to
(`_registry.json`, `_registry-required.json`, `_users.json`) or a definition kept for reference
(`_lifecycle-application.json`, not loaded by the runner). Every other `*.json` file is one fixture.

### Top-level keys

| Key | Required | Meaning |
| --- | --- | --- |
| `name` | yes | What the fixture shows, for people. Not read by the runner. |
| `registry` | yes | The registry (spec/schema/registry.schema.json), inline as an object, or a path relative to the fixture's folder (e.g. `"_registry.json"`). |
| `users` | no | The people workflows see: an array, inline or as a relative path. Each user: `id` (required), `name` (default: the id), `email`, `roles` (array), `groups` (array), `manager` (a user id). |
| `now` | no | The clock's start, an ISO 8601 instant. Default `2026-10-19T09:00:00Z`. |
| `settings` | no | Global settings (spec/schema/settings.schema.json), saved before anything else by `admin` at revision 0. |
| `records` | no | Records that exist before any workflow: `{ "<entity>": [ { "id": "a1", "<field>": value, … } ] }`. `id` is the record id, the other keys are field values. |
| `actions` | no | How app actions behave: `{ "<actionKey>": { "output": {…}, "failTimes": n, "failMessage": "…" } }`. The first `failTimes` calls of that action fail with `failMessage` (default "The action failed."); every other call returns `output` (default `{}`). Actions not listed return `{}`. |
| `webhooks` | no | Canned webhook responses per URL prefix: `{ "<urlPrefix>": [ { "status": 200, "body": <any JSON> } ] }`. A call whose URL starts with a prefix gets that list's next response; the last one repeats. Calls matching no prefix get `200` with body `{}`. No call leaves the test. |
| `definitions` | no | Definitions (spec/schema/definition.schema.json) to create and publish, in order, by `admin`. A definition that fails lint fails the fixture. |
| `steps` | yes | What happens, in order (below). |

### Setup order

1. Load the registry, start the clock at `now`, add the users.
2. Save `settings`.
3. Create the `records` (as the app would, so their `created` change events are written).
4. Configure `actions` and `webhooks`.
5. **Settle** (below), so record creation is fully processed.
6. Create and publish each of `definitions` in order (an id that already exists gets a new draft that is then published).
7. **Settle** again, then play the steps.

Records from `records` therefore exist before any definition is published: automations are not
started by their `created` events, and lifecycles reach them only by applying to existing records.

### Settle

After setup and **after every step**, the runner settles: it runs background work (the outbox of
change events and notifications, and due timers) round after round until a round does nothing.
It fails the fixture when an outbox row records an error, when a timer fails (`timerFailed` audit
entry), or after 100 rounds (a trigger loop). Settling never moves the clock.

### Steps

Each step is an object with at most one action verb, plus optional `expect` and `expectError`. A step
with only `expect` checks without acting. A step with an unknown verb (and no `expect`) fails.

Actors: where a step names a user (`actor`, `user`), the runner acts as that user id with the name
and roles from `users` (an id not in `users` acts with the id as its name and no roles). Without one,
the system acts. Admin operations (`retry`, `skip`, `cancel`, `migrate`, `settings`, `archive`,
`restore`, `permissions`) always act as the user `admin`.

| Verb | Shape | What it does |
| --- | --- | --- |
| `create` | `{ "entity", "id", "values": {…} }` | Creates a record as the app would (a `created` change event). |
| `change` | `{ "entity", "id", "values": {…}, "actor"? }` | Changes fields as the app would (an `updated` change event). With `actor`, the change is made by that user (it shows as theirs in the audit and to `who` rules). |
| `delete` | `{ "entity", "id" }` | Deletes the record as the app would. |
| `transition` | `{ "entity", "id", "transition", "input"?, "actor"?, "lifecycle"?, "comment"? }` | Applies a lifecycle transition by key, with the form's answers and remarks. |
| `reset` | `{ "entity", "id", "actor"?, "input"?, "lifecycle"?, "comment"?, "undo"?: [ids], "undoSelected"?: true }` | Starts the record's workflow over. `undo` lists reset-list item ids to take back; `undoSelected: true` takes back exactly the items the reset list selects by default. |
| `forceState` | `{ "entity", "id", "state", "reason"?, "skipSteps"?, "actor"?, "lifecycle"? }` | Moves the record to a state key as an intervention. `reason` defaults to `""`, `skipSteps` to false. |
| `decide` | `{ "step"?, "task"?, "user", "outcome", "comment"?, "data"? }` | Decides a task. The task: among tasks of node `step` (all tasks without `step`) in creation order, number `task` (0-based) when given, else the open one, else the latest one (so deciding a closed task can be tested). |
| `delegate` | `{ "step"?, "user", "to" }` | Delegates the task found as for `decide` (without `task`) to user `to`, without a note. |
| `signal` | `{ "signal", "entity"?, "id"?, "data"? }` | Raises a signal (as the system), optionally about one record. |
| `manual` | `{ "workflow", "entity"?, "id"?, "input"?, "actor"? }` | Starts a manual workflow by definition key, on a record or on none. |
| `advance` | `"PT2H"` (an ISO 8601 duration) | Moves the clock forward (below). |
| `publish` | a definition object | Creates it (or saves a new draft of the same id) and publishes it. |
| `retry` | `{ "definition", "step"? }` | Retries the stopped instance of definition key `definition` (the most recently created one, any kind). `step` picks which incident (token id or failed step id) when several paths stopped. |
| `skip` | `{ "definition", "outputs"?, "step"? }` | Skips the failed step of that instance, giving it `outputs`. |
| `cancel` | `{ "definition" }` | Cancels that instance (reason `"test"`). |
| `migrate` | `{ "definition", "id"?, "version", "stateMap"? }` | Moves an instance of definition key `definition` (record or automation, for record `id` when given; the engine's newest first) to `version`, renaming states by `stateMap` (`{ old: new }`). |
| `settings` | a settings object | Replaces the global settings (at the current revision). |
| `archive` / `restore` | `"<definitionKey>"` | Archives or restores the definition with that key. |
| `permissions` | `{ "workflow", "changes": [ { "node", "who": [...] } ], "note"? }` | Changes who may do nodes of the workflow with that key (this publishes). |

#### `advance` semantics

The duration is turned into a fixed length: **years are 365 days, months 30 days, weeks 7 days**,
then days, hours, minutes and seconds as written (`P1M` is 30 × 24 hours). The clock does not jump
straight to the end: the runner settles, moves the clock to the next timer due within the window,
settles again, and repeats until it reaches the end, so timers fire in order at their own due
time and timers they schedule within the window fire too. The duration is not calendar-aware and
ignores the work calendar; workflows themselves use the calendar for their own durations.

#### `expectError`

`"expectError": "<code>"` means the action must fail with an engine error of exactly that code
(e.g. `notAllowed`, `invalidInput`, `commentRequired`); the step then settles and its `expect` is checked.
If the action succeeds, or fails with another code, the fixture fails. Without `expectError`, any
engine error fails the fixture.

### Expectations (`expect`)

Each key is checked independently, after the step's settle.

| Key | Shape | Checked against |
| --- | --- | --- |
| `instances` | `[ { "definition", "kind", "status", "state", "record", "version", "error", "reason" } ]` | Every instance of the kinds in `instanceKinds` (default `["automation", "record"]`; add `"run"` to see child runs), in creation order (start time, then the order the audit first mentions them). The count must match exactly; each item is a subset match. `definition` is the definition key, `record` the record id. |
| `instanceKinds` | `["automation", "record", "run"]` | Which kinds `instances` lists. |
| `record` | `{ "entity", "id", "values": {…} }` or `{ "entity", "id", "deleted": true }` | The record's current field values (subset), or that it no longer exists. |
| `tasks` | `[ { "step", "status", "assignees", "outcome", "title", "due", "instructions", "escalationLevel" } ]` | Every task, open or closed, in creation order. Count exact; items subset. `step` is the node id; `assignees` is the exact ordered list of user ids. |
| `notifications` | `[ { "channel", "to", "subject", "body", "link" } ]` | Every notification delivered so far, in order (deliveries are idempotent per notification key, so a retried delivery counts once). `to` lists each recipient's user id, or email when it has no user id. Record links use the template `https://app.test/{entity}/{id}`. |
| `notificationCount` | number | How many notifications were delivered so far. |
| `actionsCalled` | `[ { "key", "input", "attempt" } ]` | Every app action call so far, in order (retries are separate calls with a growing `attempt`). |
| `webhooks` | `[ { "method", "url", "body", "headers" } ]` | Every webhook call so far, in order; `body` is the parsed JSON body. |
| `outputs` | `{ "<definitionKey>": { "<stepId>": {…} } }` | The step outputs of the most recently created instance of each key (subset). |
| `vars` | `{ "<definitionKey>": {…} }` | The variables of that instance (subset). |
| `available` | `{ "entity", "id", "user"?, "transitions": [keys], "state"?, "reset"?, "comments"?, "resetComment"? }` | What the record page offers that user: the exact ordered list of transition keys, the current state key, whether a reset is offered, each offered transition's remarks setting (`none`, `optional`, `required`; subset) and the reset's. |
| `resetPreview` | `{ "entity", "id", "user"?, "items": [ { "id", "kind", "selected", "undoable", "title", "detail" } ] }` or `{ …, "error": "<code>" }` | The reset list for that user: exact item count, in order, each a subset match; or that listing it fails with that code. |
| `audit` | `[ "category.action" or { "is": "category.action", "data"?: {…}, "actor"?: "…" } ]` | An ordered subsequence: these entries appear in this order, with any others in between. `data` is a subset match of the entry's data; `actor` matches the actor id, or the actor type (`system`). |
| `auditCount` | `{ "category.action": n }` | Exactly `n` entries of each listed kind. |
| `auditChainValid` | `true` | The whole audit chain verifies. |
| `bookmarks` | number | How many bookmarks (timers and waits) exist. |

### Matching rules

- **Objects are subsets.** Keys in the expectation must match; keys it leaves out are ignored. A key
  given as `null` expects the value to be null or absent.
- **Lists are exact.** Same length, same order, each item matched by these rules (so objects inside
  lists are subsets too).
- **Scalars** compare as workflow values (see the top of this page): `2` equals `2.0`, instants as
  `…:ss.fffZ` text, text exactly.
- The first mismatch fails the fixture, naming the file, the step number and its keys, and the path
  of the difference (e.g. `tasks[1].assignees`).

## Lint fixtures (`lint/*.json`)

| Key | Meaning |
| --- | --- |
| `name` | For people. |
| `registry` | A path relative to `lint/` (usually `../engine/_registry.json`). |
| `settings` | Optional global settings. |
| `others` | Optional other definitions that exist (for cross-definition rules such as one lifecycle per field). |
| `definition` | The definition to lint. |
| `expected` | `[ { "code", "node"?, "severity"? } ]`: exactly these problems, in any order. `severity` defaults to `error`. An entry without `node` matches that code on any node. |

## Condition and calculation fixtures (`conditions/*.json`)

Shared with the form builder, which uses the same rule language. `today` fixes the date relative
operators use. `conditions.json` cases have `name`, `condition` (or null), `values` (path → value) and
`expected` (true/false); cases using form-only operators (`visible`, `hidden`, `complete`,
`incomplete`, `rowsGte`, `rowsLte`) are skipped by workflow engines. `calc.json` cases have `name`,
`calc`, `values` (path → list of values), `rows` (path → row count) and `expected` (any value).

## Audit chain fixture (`audit/chain.json`)

`entries`: each has the hashed `entry` object, its `canonical` text (JSON Canonicalization Scheme),
the `prevHash` and the `hash` = lowercase hex `sha256(prevHash + "\n" + canonical)`. The first
`prevHash` is 64 zeros. An engine must produce the same canonical text and hash for each entry.
