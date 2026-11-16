# zouriel/workflows (Laravel): Agent Instructions

The Laravel engine for Zouriel Workflows. Read, in this order: `../spec/SPEC.md`, `../docs/plans/go-and-laravel.md`
(sections 1, 2, 4 and 6 are this package's design), then this file.

## Rules

1. **The spec and its fixtures decide.** `../spec/SPEC.md` + `../spec/schema/*.json` define meaning;
   `../spec/fixtures/**` are the tie-breaker. The .NET engine in `../dotnet` is the reference for anything
   the spec doesn't say; if a port needs new behaviour, it goes into the spec (and .NET) first.
2. **Same API, same bytes.** Serve `../spec/api/openapi.json` under `config('workflows.path')` with identical
   request/response bodies and error codes, so the @zouriel/ui components need only a different base URL.
3. **`src/Core` has no Illuminate imports** (model, values, lint, expressions, time, audit, engine, in-memory
   store). `src/Laravel` is the only place that touches Laravel (provider, Eloquent store, change capture,
   HTTP, console, jobs).
4. **Forms go through `zouriel/forms`** (conditions, calculations, form validation). Never reimplement them here.
5. **JSON:** decode with `Zouriel\Forms\Json` (objects stay `stdClass`, numbers are floats); hash with
   `Core\Json\Canonical`; store definitions and audit JSON as text, never `json`/`jsonb` columns.
6. **Progress is visible:** every spec fixture runs as a test (engine in `tests/Conformance/EngineTest.php`, lint,
   conditions and audit in their own files, each taking its file list from disk); `ProgressTest.php` fails when a
   fixture folder has no runner. Behaviour the fixtures cannot reach is ported from the .NET tests into `tests/Unit`.
7. **Every change ships with tests that pass; prove each new test can fail** by planting the fault once.
   A fixture gap you find goes into the plan's Phase 0 list and gets a unit test here meanwhile.

## Layout

| Folder | What | .NET source it ports |
|---|---|---|
| `src/Core/Json` | `Canonical` (JCS), `Values::text`, `Read`/`Write` (typed JSON reads with System.Text.Json's strictness, null-omitting writes), `JsonShapeException` | `Json/Values.cs`, `Model/Converters.cs` options |
| `src/Core/Model` | Definitions: `WorkflowDefinition`, triggers, lifecycles (states, transitions, SLA with pause fields, reset, remarks), every step type, assignees, operands, templates, conditions; `Definitions::parse()`/`serialize()` | `Model/*.cs` |
| `src/Core/Registry` | `WorkflowRegistry` + descriptors, `RegistryBuilder` (fluent), `Schema::response()` (`GET /schema` body) | `Registry/*.cs`, `WorkflowApi.PublicRegistry` |
| `src/Core/Settings` | `GlobalSettings` (`parse`, `toJson`, `problems`, `workCalendar`) | `Settings/GlobalSettings.cs` |
| `src/Core/Lint` | `Linter` (+ `LintRun`, `LintEnv`), `LintProblem`, `LintContext`, `DefinitionDiff` | `Lint/*.cs` |
| `src/Core/Time` | `IsoDuration`, `WorkCalendar` (plain and working time, DST), `Cron` (parsing, `next()`), `Instant` (ms since 1970 UTC, `MAX` = never), `Clock`, `SystemClock`, `ManualClock` | `Time/Durations.cs`, `Time/Cron.cs`, `Time/ManualTimeProvider.cs` |
| `src/Core/Host` | What the app supplies: `WorkflowIdentity`, `NotificationSender`, `WebhookClient`, `SecretResolver`, `ActionResolver` + `WorkflowAction`/`UndoableWorkflowAction`, `ActorAccessor`, `FormValidator` (default `FormsValidator` = `zouriel/forms`), and their value types (`WorkflowActor`, `WorkflowUser`, `WorkflowNotification`…) | `Abstractions.cs`, `Engine/WorkflowEngineOptions.cs` |
| `src/Core/Storage` | Records, statuses, queries, `WorkflowStore`/`WorkflowSession`/`EntityGateway` contracts, `InMemoryStore` (+ session and gateway: overlay units, row versions, live keys, claims, savepoints, per-tenant audit chains) | `Storage/Store.cs`, `Storage/Records.cs`, `Storage/InMemoryWorkflowStore.cs` |
| `src/Core/Changes` | `ChangeInspector` (change events, owned fields, initial states), `EntityChange`, `LifecycleEvent`, `WriteScope(s)` (the request's stack of engine write scopes) | `Changes/*.cs` |
| `src/Core/Engine` | `WorkflowEngine` (core: units of work with retries, ownership cache, settings) and its partials as traits in `Concerns/` (`Definitions`, `Triggers`, `Runs`, `Steps`, `Tasks`, `Lifecycles`, `SlaPause`, `Resets`, `Simulation`, `Views`); `RunState`/`Token`/`Frame`, `ValueScope`, `Uow`, `Exec`, `SlaClocks`, `CompiledDefinition`; read models in `Views/` | `Engine/*.cs` |
| `src/Core/Testing` | `WorkflowTestHost` (engine + in-memory store + manual clock + fakes), `ConformanceRunner` (`spec/fixtures/engine`) | `Zouriel.Workflows.Testing/*.cs` |
| `src/Laravel/Http` | The REST API: `WorkflowApi` (routes, default bindings), thin controllers in `Controllers/` over the engine, `IdentifyActor`/`AuthorizeOperation` middleware (401, config gates), `ActorResolver` (+ `AuthActorResolver`), `AccessPolicy` (+ `RecordAccessPolicy`, `AllowAllAccessPolicy`), `ApiJson` (.NET's JSON shapes), `RequestBody` (System.Text.Json web reading), `ApiErrors` (code → status), `AuditCsv` | `Zouriel.Workflows.AspNetCore/*` |
| `src/Core/Expressions`, `Audit`, `Support` | conditions through `zouriel/forms`; audit chain, `AuditWriter`, `Masker`; `AbsoluteUrl` (.NET `Uri` rules for origins) | `Expressions/`, `Audit/` |

Model classes read with `X::fromJson(stdClass, path)` and write with `toJson()`. Where .NET would leave a
non-nullable member null (and crash later), an absent or null value gives the member's default instead.

Engine conventions: instants are `int` milliseconds since 1970 UTC (`Instant::format` writes `…ss.fffZ`); workflow
values are `zouriel/forms` values (objects as `stdClass`, numbers as floats); stored JSON members stay text. .NET
`async` methods are synchronous here and drop the `Async` suffix. An instance is held once per unit of work (`Uow`
`execs`/`records`), as in .NET. Where an incident text names an exception type, `InvalidOperationException` keeps the
.NET wording. App action timeouts are not enforced (PHP runs the handler synchronously); webhook timeouts go to the client.

API conventions: every endpoint of `../spec/api/openapi.json` is served, and `tests/Laravel/Http/ContractTest.php` fails
when one is missing or extra or a response does not match its schema (`ApiTestCase::api()` checks every response of the
API tests). Views go out with ASP.NET's web defaults (all members, nulls included, .NET `DateTimeOffset` members as
`…+00:00`); error `details` and the JSON audit export leave null members out (.NET's document options). Engine errors
are mapped in the base controller, never in the app's exception handler.

## Commands

```
composer install
composer test
composer lint
```
