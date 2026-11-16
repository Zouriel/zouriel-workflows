# zouriel/workflows (Laravel)

Status: **in progress** on branch `laravel-engine`. Ported so far (framework-free, under `src/Core`): canonical
JSON (RFC 8785), the audit hash chain and its verifier, workflow conditions (through `zouriel/forms`), the
definition model and its parsing, the registry (fluent builder, JSON form, `GET /schema` body), global settings
and their checks, ISO durations, the work calendar and cron schedules, the linter (all `spec/fixtures/lint`
pass) and the version diff, and the **runtime engine with an in-memory store**: definitions (drafts, publish,
versions, rollback, archive, permissions), change inspection, the outbox, every step type, tasks with
maker-checker, lifecycles with SLA pause and resume, resets with undo, timers with parking, signals, manual
starts, test runs and views. All 58 `spec/fixtures/engine` files pass through the conformance runner.
Eloquent storage and change capture, the REST API and the worker follow the phases in
`../docs/plans/go-and-laravel.md`.

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
