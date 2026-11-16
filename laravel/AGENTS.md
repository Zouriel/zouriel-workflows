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
6. **Progress is visible:** every spec fixture is either a passing test or a named `todo` in
   `tests/Conformance/ProgressTest.php`. When you port a part, replace its todos with real runs.
7. **Every change ships with tests that pass; prove each new test can fail** by planting the fault once.
   A fixture gap you find goes into the plan's Phase 0 list and gets a unit test here meanwhile.

## Layout

| Folder | What | .NET source it ports |
|---|---|---|
| `src/Core/Json` | `Canonical` (JCS), `Values::text`, `Read`/`Write` (typed JSON reads with System.Text.Json's strictness, null-omitting writes), `JsonShapeException` | `Json/Values.cs`, `Model/Converters.cs` options |
| `src/Core/Model` | Definitions: `WorkflowDefinition`, triggers, lifecycles (states, transitions, SLA with pause fields, reset, remarks), every step type, assignees, operands, templates, conditions; `Definitions::parse()`/`serialize()` | `Model/*.cs` |
| `src/Core/Registry` | `WorkflowRegistry` + descriptors, `RegistryBuilder` (fluent), `Schema::response()` (`GET /schema` body) | `Registry/*.cs`, `WorkflowApi.PublicRegistry` |
| `src/Core/Settings` | `GlobalSettings` (`parse`, `toJson`, `problems`) | `Settings/GlobalSettings.cs` |
| `src/Core/Lint` | `Linter` (+ `LintRun`, `LintEnv`), `LintProblem`, `LintContext`, `DefinitionDiff` | `Lint/*.cs` |
| `src/Core/Time` | `IsoDuration`, `Cron` (parsing; the work calendar and `next()` come with the engine) | `Time/Durations.cs`, `Time/Cron.cs` |
| `src/Core/Engine` | `CompiledDefinition` (step lists by id) | `Engine/CompiledDefinition.cs` |
| `src/Core/Expressions`, `Audit`, `Support` | conditions through `zouriel/forms`; audit chain; `AbsoluteUrl` (.NET `Uri` rules for origins) | `Expressions/`, `Audit/` |

Model classes read with `X::fromJson(stdClass, path)` and write with `toJson()`. Where .NET would leave a
non-nullable member null (and crash later), an absent or null value gives the member's default instead.

## Commands

```
composer install
composer test
composer lint
```
