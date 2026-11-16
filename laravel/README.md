# zouriel/workflows (Laravel)

Status: **in progress** on branch `laravel-engine`. Ported so far (framework-free, under `src/Core`): canonical
JSON (RFC 8785), the audit hash chain and its verifier, workflow conditions (through `zouriel/forms`), the
definition model and its parsing, the registry (fluent builder, JSON form, `GET /schema` body), global settings
and their checks, ISO durations, the work calendar and cron schedules, the linter (all `spec/fixtures/lint`
pass) and the version diff, and the **runtime engine with an in-memory store**: definitions (drafts, publish,
versions, rollback, archive, permissions), change inspection, the outbox, every step type, tasks with
maker-checker, lifecycles with SLA pause and resume, resets with undo, timers with parking, signals, manual
starts, test runs and views. All 58 `spec/fixtures/engine` files pass through the conformance runner.
Phase 2 adds the **database store** (all engine fixtures also pass on SQLite and
PostgreSQL), **Eloquent change capture** (`HasWorkflows`), the record gateway over models, the service provider and
the worker commands. The REST API follows in phase 3 (`../docs/plans/go-and-laravel.md`).

Running the engine (here with the in-memory store and the test host's fakes; apps get it from the
service provider, see below):

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

## Using it in a Laravel app

1. Publish and run the migration (`php artisan vendor:publish --tag=workflows-migrations`; the tables use
   `config('workflows.table_prefix')`, default `wf_`, on `config('workflows.connection')`, which must be the
   connection of the registered models). Optionally publish the config (`--tag=workflows-config`).
2. Add `Zouriel\Workflows\Laravel\Changes\HasWorkflows` to every registered model. Its writes (`save`, `delete`,
   `increment`, `create`, `update`) then fill initial lifecycle states, refuse writes to owned fields
   (`FieldOwnedException`), and add the change event to the outbox in the write's own transaction (opened when none
   is). Not seen: `Model::query()->update()`, `DB::table()`, `saveQuietly()`, `withoutEvents()`, `upsert()`; report
   those with `app(ModelChanges::class)->record($entity, $id, $kind, $before, $after)`.
3. Declare the registry in a class named by `config('workflows.registry')`:

```php
use Zouriel\Workflows\Core\Registry\{EntityBuilder as E, FieldBuilder as F, ActionBuilder as A};
use Zouriel\Workflows\Laravel\{WorkflowRegistrar, Registry\ModelRegistry};

final class AppWorkflows implements WorkflowRegistrar
{
    public function register(ModelRegistry $registry): void
    {
        $registry
            ->entity('application', Application::class, fn (E $e) => $e->label('Application', 'Applications')->title('title')
                ->field('status', fn (F $f) => $f->enum(ApplicationStatus::class)->lifecycleCapable())   // enum cast
                ->field('amount', fn (F $f) => $f->currency()->watchable()->settable())                  // decimal cast → float
                ->field('name', fn (F $f) => $f->json()->settable())                                      // translatable → {en, dv}
                ->field('programme.name', fn (F $f) => $f->key('programmeName')->json())                  // read through a relation
                ->relation('programme', 'programme', fn (F $f) => $f->settable())                         // BelongsTo: its foreign key
                ->collection('documents', 'document'))                                                    // HasMany, for forEach
            ->entity('programme', Programme::class)
            ->entity('document', Document::class, fn (E $e) => $e->field('verified', fn (F $f) => $f->boolean()->settable()))
            ->action('finance.createInvoice', CreateInvoice::class, fn (A $a) => $a->label('Create invoice')->for('application'))
            ->role('reviewer', 'Reviewer');
    }
}
```

4. Bind the host services in `config/workflows.php`: `identity` (a `Core\Host\WorkflowIdentity` class: people, roles,
   groups, managers), `actor` (default: `Auth::user()`), `notifications.sender` (default: Laravel Notifications to
   `notifications.recipients`, a `NotifiableResolver`; default: `notifications.user_model` by id, else mail),
   `webhooks` (default: Laravel's HTTP client, no redirects, private networks refused, 1 MB cap), `secrets`,
   `tenant`, `record_link`. Action handlers (`Core\Host\WorkflowAction`) are made by the container.
5. Run the worker: `php artisan workflows:work` under Supervisor (stops on SIGTERM, `--max-time`, `--memory`), or
   `Schedule::command('workflows:tick --until-idle')->everyMinute()->withoutOverlapping()`. Seed definitions with
   `php artisan workflows:seed path/to/definitions` (create and publish; running it again changes nothing).
6. Use the engine from the container: `app(WorkflowEngine::class)->applyTransition('application', $id, 'submit', null,
   WorkflowActor::user($user->id, $user->name))`. It is scoped per request/job; a unit of work run inside the app's
   own transaction commits into it.

## Installing the package

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
