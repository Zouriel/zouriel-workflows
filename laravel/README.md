# zouriel/workflows (Laravel)

Status: **in progress** on branch `laravel-engine`. Ported so far (framework-free, under `src/Core`): canonical
JSON (RFC 8785), the audit hash chain and its verifier, workflow conditions (through `zouriel/forms`), the
definition model and its parsing, the registry (fluent builder, JSON form, `GET /schema` body), global settings
and their checks, ISO durations, the work calendar and cron schedules, the linter (all `spec/fixtures/lint`
pass) and the version diff, and the **runtime engine with an in-memory store**: definitions (drafts, publish,
versions, rollback, archive, permissions), change inspection, the outbox, every step type, tasks with
maker-checker, lifecycles with SLA pause and resume, resets with undo, timers with parking, signals, manual
starts, test runs and views. All 58 `spec/fixtures/engine` files pass through the conformance runner. The **REST API**
(every operation of `../spec/api/openapi.json`) is served from the container's `WorkflowEngine`. Eloquent storage and
change capture and the worker follow the phases in `../docs/plans/go-and-laravel.md`.

Running the engine (here with the in-memory store and the test host's fakes; apps will get it from the
service provider once the Eloquent store exists):

```php
use Zouriel\Workflows\Core\Testing\WorkflowTestHost;

$host = new WorkflowTestHost($registry);              // in-memory store, manual clock, recording fakes
$host->publish($definitionJson);                      // create + publish (lint errors refuse it)
$host->store->createRecord('application', 'a1', (object) ['title' => 'Q']);
$host->settle();                                      // outbox and due timers, as the worker would
$view = $host->engine->applyTransition('application', 'a1', 'submit', null, WorkflowActor::user('ali', 'Ali'));
$host->advance(2 * 86400000);                         // fire timers due in the next two days
```

Declaring a registry (framework-free; the Laravel layer will bind fields to Eloquent attributes):

```php
use Zouriel\Workflows\Core\Registry\{RegistryBuilder, EntityBuilder as E, FieldBuilder as F};

$registry = (new RegistryBuilder)
    ->entity('application', fn (E $e) => $e->label('Application', 'Applications')->title('title')
        ->field('status', fn (F $f) => $f->enum(ApplicationStatus::class)->lifecycleCapable())
        ->field('amount', fn (F $f) => $f->currency()->watchable()->settable())
        ->field('applicant_email', fn (F $f) => $f->key('applicantEmail')->email()->sensitive())
        ->relation('programme', 'programme')
        ->collection('documents', 'document'))
    ->entity('programme')->entity('document')
    ->action('finance.createInvoice', CreateInvoice::class, fn ($a) => $a->label('Create invoice')->for('application')
        ->input('amount', 'Amount', 'currency', required: true)->output('invoiceNo', 'Invoice number'))
    ->signal('payment.received', fn ($s) => $s->for('application')->field('amount', 'Amount', 'currency'))
    ->role('reviewer', 'Reviewer')->department('licensing', 'Licensing')
    ->build();

$problems = Linter::lint(Definitions::parse($json), new LintContext($registry, GlobalSettings::parse($settingsJson), $otherPublished));
```

## The REST API

The service provider mounts the API the `@zouriel/ui` workflow components call (`provideUiWorkflow({ baseUrl })`) at
`config('workflows.path')` (default `api/workflows`). It calls the `WorkflowEngine` bound in the container, so it works
with any store (in tests, the in-memory one: `$app->instance(WorkflowEngine::class, $host->engine)`). Bodies, error codes
and statuses are those of the .NET API: views with every member (nulls included), errors as `{ code, message, details? }`
(`notAllowed`/`segregationOfDuties` 403, `notFound`/`noRecord`/`definitionNotFound` 404, conflicts 409, `tooLarge` 413,
everything else 400), `401 unauthenticated` for callers who are not signed in.

```php
// config/workflows.php (php artisan vendor:publish --tag=workflows-config)
'path' => 'api/workflows',
'api' => [
    'middleware' => ['api', 'auth:sanctum'],   // the app's authentication
    'allow_anonymous' => false,
    'gates' => [                               // Gate ability per kind of operation; null = any signed-in user
        'view' => null, 'edit' => 'workflows.edit', 'publish' => 'workflows.publish', 'intervene' => 'workflows.intervene',
        'audit' => 'workflows.audit', 'audit_export' => null /* falls back to audit */, 'settings' => 'workflows.settings',
        'signal' => null,                      // falls back to intervene; with neither, nobody may raise signals here
    ],
    'max_body_bytes' => 2 * 1024 * 1024, 'max_decision_data_bytes' => 256 * 1024, 'max_simulation_events' => 50,
],
```

```php
// AppServiceProvider::boot()
Gate::define('workflows.edit', fn (User $user) => $user->can('manage-workflows'));
// The caller as a workflow actor (default: auth()->user(): id, `name`, roles from a workflowRoles() method):
$this->app->bind(ActorResolver::class, fn () => new AuthActorResolver(roles: fn (User $u) => $u->roles->pluck('key')->all()));
// Which records, instances and tasks a person may see (default: all; extend RecordAccessPolicy):
$this->app->bind(AccessPolicy::class, AppWorkflowAccess::class);
```

`GET /me` reports the gates as `can`. To mount the API yourself, set `workflows.api.routes` to false and call
`Zouriel\Workflows\Laravel\Http\WorkflowApi::routes('admin/workflows', ['web', 'auth'])`.

Install in an app. This folder is published as the private, read-only split repo
`Zouriel/workflows-laravel` (from this branch, with `git subtree split --prefix=laravel`):

```json
"repositories": [
  { "type": "vcs", "url": "git@github.com:Zouriel/zouriel-forms.git" },
  { "type": "vcs", "url": "git@github.com:Zouriel/workflows-laravel.git" }
],
"require": { "zouriel/workflows": "dev-main" }
```

Never commit to the split repo; change `laravel/` here and split again. Conformance tests need `../spec`,
so they only run here (they skip in the split repo).
