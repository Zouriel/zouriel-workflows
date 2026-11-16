<?php

namespace Zouriel\Workflows\Tests\Support;

use Illuminate\Database\Schema\Blueprint;
use Illuminate\Support\Facades\DB;
use Illuminate\Support\Facades\Schema;
use LogicException;
use Zouriel\Workflows\Core\Engine\WorkflowEngine;
use Zouriel\Workflows\Core\Host\WorkflowActor;
use Zouriel\Workflows\Core\Host\WorkflowIdentity;
use Zouriel\Workflows\Core\Host\WorkflowUser;
use Zouriel\Workflows\Core\Lint\LintProblem;
use Zouriel\Workflows\Core\Model\Definitions;
use Zouriel\Workflows\Core\Testing\FakeIdentity;
use Zouriel\Workflows\Core\Time\Clock;
use Zouriel\Workflows\Core\Time\Instant;
use Zouriel\Workflows\Core\Time\ManualClock;
use Zouriel\Workflows\Core\WorkflowException;
use Zouriel\Workflows\Laravel\Worker;
use Zouriel\Workflows\Tests\Support\App\TestRegistrar;

/**
 * A Laravel app wired as a real one: the test registry bound to Eloquent models (applications, programmes, documents),
 * the engine from the service provider on the database store, a manual clock and a fake directory.
 */
abstract class EloquentTestCase extends DatabaseTestCase
{
    protected ManualClock $clock;

    protected FakeIdentity $identity;

    protected function defineEnvironment($app): void
    {
        parent::defineEnvironment($app);
        $app['config']->set('workflows.registry', TestRegistrar::class);
        $this->clock = new ManualClock(Instant::parse('2026-10-19T09:00:00Z'));
        $this->identity = new FakeIdentity;
        $this->identity->add(new WorkflowUser('ali', 'Ali'));
        $this->identity->add(new WorkflowUser('rev1', 'Rana', 'rana@example.com', ['reviewer']));
        $app->instance(Clock::class, $this->clock);
        $app->instance(WorkflowIdentity::class, $this->identity);
    }

    protected function setUp(): void
    {
        parent::setUp();
        foreach (['documents', 'applications', 'programmes'] as $table) {
            Schema::dropIfExists($table);
        }
        Schema::create('programmes', function (Blueprint $t) {
            $t->id();
            $t->text('name');
            $t->string('coordinator_id')->nullable();
        });
        Schema::create('applications', function (Blueprint $t) {
            $t->id();
            $t->string('title');
            $t->string('status')->nullable();
            $t->string('stage')->nullable();
            $t->decimal('amount', 10, 2)->default(0);
            $t->text('notes')->nullable();
            $t->string('applicant_id')->nullable();
            $t->date('deadline')->nullable();
            $t->foreignId('programme_id')->nullable();
        });
        Schema::create('documents', function (Blueprint $t) {
            $t->uuid('id')->primary();
            $t->foreignId('application_id');
            $t->string('name');
            $t->boolean('verified')->default(false);
        });
    }

    protected function engine(): WorkflowEngine
    {
        return $this->app->make(WorkflowEngine::class);
    }

    protected function admin(): WorkflowActor
    {
        return WorkflowActor::user('admin', 'Admin');
    }

    /** Creates and publishes a definition; lint errors fail the test with their messages. */
    protected function publish(string $json): string
    {
        $definition = Definitions::parse($json);
        $this->engine()->createDefinition($json, $this->admin());
        try {
            $this->engine()->publish($definition->id, null, $this->admin());
        } catch (WorkflowException $e) {
            throw new LogicException("'{$definition->key}' cannot be published: ".implode(' ', array_map(fn (LintProblem $p) => "[{$p->code}] {$p->message}", (array) $e->details)));
        }

        return $definition->id;
    }

    /** Runs the worker until nothing is left; a failed outbox row fails the test. */
    protected function settle(): void
    {
        $worker = $this->app->make(Worker::class);
        for ($i = 0; $i < 50; $i++) {
            $handled = $worker->round();
            $failed = DB::table('wf_outbox')->whereNotNull('error')->value('error');
            if ($failed !== null) {
                throw new LogicException("Outbox failed: {$failed}");
            }
            if ($handled === 0) {
                return;
            }
        }
        throw new LogicException('Background work did not settle.');
    }
}
