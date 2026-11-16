<?php

namespace Zouriel\Workflows\Tests\Support\App;

use Zouriel\Workflows\Core\Registry\ActionBuilder;
use Zouriel\Workflows\Core\Registry\EntityBuilder;
use Zouriel\Workflows\Core\Registry\FieldBuilder;
use Zouriel\Workflows\Laravel\Registry\ModelRegistry;
use Zouriel\Workflows\Laravel\WorkflowRegistrar;

/** The registry of the test app, as an app declares it (config('workflows.registry')). */
final class TestRegistrar implements WorkflowRegistrar
{
    public function register(ModelRegistry $registry): void
    {
        $registry
            ->entity('programme', Programme::class, fn (EntityBuilder $e) => $e->title('name')
                ->field('name', fn (FieldBuilder $f) => $f->json()->settable())
                ->field('coordinator_id', fn (FieldBuilder $f) => $f->key('coordinator')->user()))
            ->entity('application', Application::class, fn (EntityBuilder $e) => $e->label('Application', 'Applications')->title('title')
                ->field('title', fn (FieldBuilder $f) => $f->settable())
                ->field('status', fn (FieldBuilder $f) => $f->enum(ApplicationStatus::class)->lifecycleCapable())
                ->field('stage', fn (FieldBuilder $f) => $f->options(['new' => 'New', 'checked' => 'Checked', 'done' => 'Done'])->watchable()->settable())
                ->field('amount', fn (FieldBuilder $f) => $f->currency()->watchable()->settable())
                ->field('notes', fn (FieldBuilder $f) => $f->settable())
                ->field('applicant_id', fn (FieldBuilder $f) => $f->key('applicant')->user())
                ->field('deadline', fn (FieldBuilder $f) => $f->date()->watchable()->settable())
                ->field('programme.name', fn (FieldBuilder $f) => $f->key('programmeName')->json())
                ->relation('programme', 'programme', fn (FieldBuilder $f) => $f->watchable()->settable())
                ->collection('documents', 'document'))
            ->entity('document', Document::class, fn (EntityBuilder $e) => $e->title('name')
                ->field('name')
                ->field('verified', fn (FieldBuilder $f) => $f->boolean()->settable()))
            ->action('stamp', StampNotes::class, fn (ActionBuilder $a) => $a->label('Stamp')->for('application')
                ->input('text', 'Text', required: true)->output('length', 'Length', 'number'))
            ->action('stampThenFail', StampThenFail::class, fn (ActionBuilder $a) => $a->label('Stamp then fail')->for('application'))
            ->role('reviewer', 'Reviewer');
    }
}
