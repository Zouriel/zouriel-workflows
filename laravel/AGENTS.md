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

## Commands

```
composer install
composer test
composer lint
```
