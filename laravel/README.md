# zouriel/workflows (Laravel)

Status: **in progress** on branch `laravel-engine`. Ported so far: canonical JSON (RFC 8785), the audit hash
chain and its verifier, workflow conditions (through `zouriel/forms`), the definition model and its parsing,
the registry (fluent builder, JSON form, `GET /schema` body), global settings and their checks, ISO durations
and cron parsing, the linter (all `spec/fixtures/lint` pass) and the version diff, plus the service provider.
The engine (work calendar, runs, in-memory store), Eloquent storage, change capture, REST API and worker follow
the phases in `../docs/plans/go-and-laravel.md`. `composer test` lists every shared fixture not passed yet as a todo.

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
