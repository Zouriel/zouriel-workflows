<?php

use Zouriel\Forms\Json;
use Zouriel\Workflows\Core\Registry\ActionBuilder;
use Zouriel\Workflows\Core\Registry\EntityBuilder;
use Zouriel\Workflows\Core\Registry\FieldBuilder;
use Zouriel\Workflows\Core\Registry\RegistryBuilder;
use Zouriel\Workflows\Core\Registry\Schema;
use Zouriel\Workflows\Core\Registry\SignalBuilder;
use Zouriel\Workflows\Core\Registry\WorkflowRegistry;

// Expectations from .NET Registry/RegistryBuilder.cs, WorkflowApi.PublicRegistry and SeparationOfDutiesTests.

enum ReviewStatus: string
{
    case Draft = 'draft';
    case UnderReview = 'underReview';
}

enum Track
{
    case Passport;
}

function sampleRegistry(): WorkflowRegistry
{
    return (new RegistryBuilder)
        ->entity('application', fn (EntityBuilder $e) => $e->label('Application', 'Applications')->title('title')
            ->field('title', fn (FieldBuilder $f) => $f->watchable())
            ->field('status', fn (FieldBuilder $f) => $f->enum(ReviewStatus::class)->lifecycleCapable())
            ->field('applicant_email', fn (FieldBuilder $f) => $f->key('applicantEmail')->email()->sensitive())
            ->field('secret_note', fn (FieldBuilder $f) => $f->hidden())
            ->relation('programme', 'programme')
            ->collection('documents', 'programme'))
        ->entity('programme')
        ->action('finance.createInvoice', 'App\\CreateInvoice', fn (ActionBuilder $a) => $a->for('application')
            ->input('amount', 'Amount', 'currency', required: true)->output('invoiceNo', 'Invoice number'))
        ->signal('payment.received', fn (SignalBuilder $s) => $s->for('application')->field('amount', 'Amount', 'currency'))
        ->role('reviewer', 'Reviewer')
        ->build();
}

it('builds what the fluent calls describe, with .NET defaults', function () {
    $r = sampleRegistry();
    $app = $r->entity('application');

    expect([$app->label, $app->pluralLabel, $app->titleField])->toBe(['Application', 'Applications', 'title'])
        ->and($r->entity('programme')->label)->toBe('Programme')
        ->and($app->field('status')->isLifecycle())->toBeTrue()
        ->and($app->field('status')->isWatchable())->toBeTrue()
        ->and(array_map(fn ($o) => [$o->value, $o->label], $app->field('status')->options))->toBe([['draft', 'Draft'], ['underReview', 'Under review']])
        ->and([$app->field('applicantEmail')->label, $app->field('applicantEmail')->type, $app->field('applicantEmail')->attribute])->toBe(['Applicant email', 'email', 'applicant_email'])
        ->and([$app->field('programme')->type, $app->field('programme')->entity])->toBe(['relation', 'programme'])
        ->and($r->action('finance.createInvoice')->label)->toBe('Create invoice')
        ->and($r->action('finance.createInvoice')->handler)->toBe('App\\CreateInvoice')
        ->and($r->signal('payment.received')->label)->toBe('Received')
        ->and(array_map(fn ($c) => $c->key, $r->channels))->toBe(['email', 'inApp']);
});

it('stores a pure enum by its case names', function () {
    $r = (new RegistryBuilder)->entity('a', fn (EntityBuilder $e) => $e->field('track', fn (FieldBuilder $f) => $f->enum(Track::class)))->build();

    expect(Json::encode($r->field('a', 'track')->toJson()))->toBe('{"key":"track","label":"Track","type":"enum","options":[{"value":"Passport","label":"Passport"}]}');
});

it('humanises keys like .NET', function (string $name, string $label) {
    expect(RegistryBuilder::humanize($name))->toBe($label);
})->with([['applicantEmail', 'Applicant email'], ['applicant_email', 'Applicant email'], ['IDNumber', 'Idnumber'], ['x2Y', 'X2 y'], ['_', '_']]);

it('refuses registrations .NET refuses', function (Closure $build, string $message) {
    expect($build)->toThrow(LogicException::class, $message);
})->with([
    'an entity twice' => [fn () => (new RegistryBuilder)->entity('a')->entity('a')->build(), "Entity 'a' is registered twice."],
    'a field twice' => [fn () => (new RegistryBuilder)->entity('a', fn ($e) => $e->field('x')->field('y', fn ($f) => $f->key('x')))->build(), "Field 'x' is registered twice on 'a'."],
    'a lifecycle field that is not an enum' => [fn () => (new RegistryBuilder)->entity('a', fn ($e) => $e->field('x', fn ($f) => $f->lifecycleCapable()))->build(), "Field 'x' is marked lifecycleCapable but is not an enum field."],
    'a relation to an unregistered entity' => [fn () => (new RegistryBuilder)->entity('a', fn ($e) => $e->relation('b', 'b'))->build(), "'a' refers to 'b', which is not a registered workflow entity."],
]);

it('serves departments, portals and queues only when there are any', function () {
    $plain = Json::encode((new RegistryBuilder)->build()->toJson());
    $r = (new RegistryBuilder)->department('licensing', 'Licensing department')->portal('staff', 'Staff portal')->queue('frontDesk', 'Front desk queue')->build();
    $back = WorkflowRegistry::fromJson(Json::encode($r->toJson()));

    expect($plain)->not->toContain('departments')->not->toContain('portals')->not->toContain('queues')
        ->and($back->departments[0]->label)->toBe('Licensing department')
        ->and($back->portals[0]->key)->toBe('staff')
        ->and($back->queues[0]->label)->toBe('Front desk queue');
});

it('reads the JSON form, a null option value as empty text', function () {
    $r = WorkflowRegistry::fromJson('{ "entities": [ { "key": "a", "label": "A", "fields": [ { "key": "k", "label": "K", "type": "enum", "options": [ { "value": null, "label": "None" }, { "value": 2, "label": "Two" } ] } ] } ] }');

    expect(array_map(fn ($o) => $o->value, $r->field('a', 'k')->options))->toBe(['', 2.0])
        ->and($r->field('a', 'k')->optionLabel(2))->toBe('Two')
        ->and($r->field('a', 'k')->optionLabel('3'))->toBe('3')
        ->and(fn () => WorkflowRegistry::fromJson('{ "entities": {} }'))->toThrow(JsonException::class);
});

it('serves GET /schema as .NET does: editors\' fields only, null members written, every step type', function () {
    $schema = Schema::response(sampleRegistry());
    $fields = array_map(fn ($f) => $f->key, $schema->registry->entities[0]->fields);

    expect($schema->specVersion)->toBe(1)
        ->and($fields)->toBe(['title', 'status', 'applicantEmail', 'programme', 'documents'])
        ->and(property_exists($schema->registry->entities[0]->fields[0], 'options'))->toBeTrue()
        ->and($schema->registry->entities[0]->fields[0]->options)->toBeNull()
        ->and(property_exists($schema->registry, 'departments'))->toBeTrue()
        ->and(count($schema->steps))->toBe(17)
        ->and(Json::encode($schema->steps[4]))->toBe('{"type":"approval","label":"Approval","outputs":["outcome","decidedBy","comments","data"]}');
});
