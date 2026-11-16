<?php

namespace Zouriel\Workflows\Core\Engine\Concerns;

use stdClass;
use Zouriel\Forms\Json;
use Zouriel\Workflows\Core\Engine\EngineOptions;
use Zouriel\Workflows\Core\Engine\LifecycleSelection;
use Zouriel\Workflows\Core\Engine\ReadOnlyGateway;
use Zouriel\Workflows\Core\Engine\SimulationEvent;
use Zouriel\Workflows\Core\Engine\SimulationLog;
use Zouriel\Workflows\Core\Engine\SimulationRequest;
use Zouriel\Workflows\Core\Engine\TriggerInfo;
use Zouriel\Workflows\Core\Engine\Uow;
use Zouriel\Workflows\Core\Engine\Views\SimulationEntry;
use Zouriel\Workflows\Core\Engine\Views\SimulationResult;
use Zouriel\Workflows\Core\Engine\WorkflowEngine;
use Zouriel\Workflows\Core\Host\WorkflowActor;
use Zouriel\Workflows\Core\Json\Values;
use Zouriel\Workflows\Core\Lint\LintProblem;
use Zouriel\Workflows\Core\Model\Definitions as DefinitionJson;
use Zouriel\Workflows\Core\Model\TriggerTypes;
use Zouriel\Workflows\Core\Model\WorkflowDefinition;
use Zouriel\Workflows\Core\Storage\DefinitionRecord;
use Zouriel\Workflows\Core\Storage\DefinitionStatus;
use Zouriel\Workflows\Core\Storage\DefinitionVersionRecord;
use Zouriel\Workflows\Core\Storage\InMemorySession;
use Zouriel\Workflows\Core\Storage\InMemoryStore;
use Zouriel\Workflows\Core\Storage\InstanceQuery;
use Zouriel\Workflows\Core\Storage\StoredRecord;
use Zouriel\Workflows\Core\Storage\TaskQuery;
use Zouriel\Workflows\Core\Storage\TaskStatus;
use Zouriel\Workflows\Core\Time\Instant;
use Zouriel\Workflows\Core\Time\IsoDuration;
use Zouriel\Workflows\Core\Time\ManualClock;
use Zouriel\Workflows\Core\WorkflowException;

/**
 * Test runs (`POST /simulate`): a definition (usually the draft) runs against a real or made-up record in a private
 * in-memory engine with a manual clock; records are read from the app, but writes, notifications, webhooks and actions
 * stay inside the run and are reported in its log. Port of .NET `Engine/WorkflowEngine.Simulate.cs`.
 */
trait Simulation
{
    /** The most events one test run may replay. */
    public const int MAX_SIMULATION_EVENTS = 50;

    public function simulate(string $definitionJson, SimulationRequest $request, WorkflowActor $actor): SimulationResult
    {
        if (count($request->events) > self::MAX_SIMULATION_EVENTS) {
            throw new WorkflowException('tooManyEvents', 'A test run can have at most '.self::MAX_SIMULATION_EVENTS.' events.');
        }
        // Bad input is refused up front (not reported as a run that failed half way).
        foreach ($request->events as $event) {
            if ($event->type === 'advance') {
                IsoDuration::parse($event->duration ?? 'PT1H');
            }
        }
        $problems = $this->validate($definitionJson);
        if (array_any($problems, fn (LintProblem $p) => $p->severity === 'error')) {
            return new SimulationResult($problems, [], [], [], null, 'Fix the problems before testing.');
        }
        $definition = DefinitionJson::parse($definitionJson);
        // A lifecycle tested on a record that would not get it (spec §6 applies to) still runs, with a warning.
        $notSelected = $this->lifecycleNotSelected($definition, $request);
        if ($notSelected !== null) {
            $problems[] = $notSelected;
        }
        [$sim, $store, $clock, $log] = $this->createSimulation($definition, $definitionJson);
        $error = null;
        try {
            if ($definition->entity !== null && $request->entityId !== null && $request->values !== null && get_object_vars($request->values) !== []) {
                $values = $store->fallback->get($definition->entity, $request->entityId)?->values ?? new stdClass;
                foreach (get_object_vars($request->values) as $key => $value) {
                    $values->{$key} = Json::normalize($value);
                }
                self::putRecordDirect($store, $definition->entity, $request->entityId, $values);
            }
            $sim->unit(fn (Uow $u) => self::startSimulation($sim, $store, $u, $definition, $request, $actor));
            self::drain($sim);
            foreach ($request->events as $event) {
                self::applySimulationEvent($sim, $store, $clock, $definition, $request, $event);
                self::drain($sim);
            }
        } catch (WorkflowException $e) {
            $error = $e->getMessage();
            $log->add('error', $e->errorCode, null, $e->getMessage());
        }

        return $this->collectSimulation($sim, $store, $log, $definition, $request, $problems, $error);
    }

    /** A `lifecycleNotSelected` warning when the test record would not get this lifecycle (pinned to another, `appliesWhen` unmet, or another comes first). */
    private function lifecycleNotSelected(WorkflowDefinition $definition, SimulationRequest $request): ?LintProblem
    {
        $lc = $definition->lifecycle;
        $entity = $definition->entity;
        $id = $request->entityId;
        if (! $definition->isLifecycle() || $lc === null || $entity === null || $id === null) {
            return null;
        }
        $warn = fn (string $message) => new LintProblem('lifecycleNotSelected', 'warning', null, $message);

        return $this->unit(function (Uow $u) use ($definition, $request, $lc, $entity, $id, $warn) {
            foreach ($this->liveRecordInstances($u, $entity, $id) as $i) {
                if ($i->definitionId === $definition->id) {
                    return null;
                }
                $other = $this->compiled($u, $i->definitionId, $i->version);
                if ($other->lifecycle()->field === $lc->field) {
                    return $warn("This record already follows '{$other->definition->title}' for {$lc->field}, so it would never get this lifecycle. The test run shows what would happen if it did.");
                }
            }
            $values = $u->s->entities()->get($entity, $id)?->values ?? new stdClass;
            foreach (Values::members($request->values) as $key => $value) {
                $values->{$key} = $value;
            }
            $triggers = array_values(array_filter($u->s->triggers(), fn ($t) => $t->type === 'lifecycle' && $t->entity === $entity && $t->field === $lc->field));
            // This definition takes its place by its first publish time (a draft that was never published comes last).
            $mine = array_find($triggers, fn ($t) => $t->definitionId === $definition->id)?->publishedAt ?? Instant::MAX;
            $candidates = array_map(fn ($t) => [$t->definitionId, $t, self::appliesWhenOf($t), $t->publishedAt], array_values(array_filter($triggers, fn ($t) => $t->definitionId !== $definition->id)));
            $candidates[] = [$definition->id, null, $lc->appliesWhen, $mine];
            foreach (self::inSelectionOrder($candidates, fn ($x) => $x[2] !== null, fn ($x) => $x[3], fn ($x) => $x[0]) as [, $trigger, $when]) {
                if (! LifecycleSelection::applies($when, $values, $u->today)) {
                    continue;
                }
                if ($trigger === null) {
                    return null;
                }
                $title = $this->compiled($u, $trigger->definitionId, $trigger->version)->definition->title;

                return $warn("This record would get '{$title}' instead of this lifecycle (it comes first for these values). The test run shows what would happen if it got this one.");
            }

            return $warn("This record does not meet this lifecycle's “Applies to” condition, so it would never get this lifecycle. The test run shows what would happen if it did.");
        });
    }

    /** @return array{0: WorkflowEngine, 1: InMemoryStore, 2: ManualClock, 3: SimulationLog} a test run's private engine */
    private function createSimulation(WorkflowDefinition $definition, string $json): array
    {
        $clock = new ManualClock($this->now());
        $log = new SimulationLog(fn () => $clock->now());
        // Real records are read one short unit at a time, so a test run never holds a database transaction open.
        $store = new InMemoryStore($this->registry, $clock);
        $store->fallback = new ReadOnlyGateway($this->options->store);
        $o = $this->options;
        $sim = new WorkflowEngine(new EngineOptions($this->registry, $store, $o->identity, clock: $clock, tenant: $o->tenant, maxStepsPerRun: $o->maxStepsPerRun,
            taskNotificationChannel: $o->taskNotificationChannel, notifyTaskAssignees: $o->notifyTaskAssignees, formValidator: $o->formValidator, simulationLog: $log,
            recordLinkTemplate: $o->recordLinkTemplate));
        $store->changes = $sim->changes;
        $this->seedSimulation($store, $definition, $json);

        return [$sim, $store, $clock, $log];
    }

    /** Copies what a test run needs: settings, published workflows (and lifecycles' triggers), and the definition under test as version 1. */
    private function seedSimulation(InMemoryStore $store, WorkflowDefinition $definition, string $json): void
    {
        $real = $this->options->store->begin();
        $s = $store->begin();
        $settings = $real->settings();
        if ($settings !== null) {
            $s->saveSettings($settings);
        }
        foreach ($real->definitions() as $d) {
            if ($d->id === $definition->id || $d->version === 0 || $d->status === DefinitionStatus::ARCHIVED) {
                continue;
            }
            $s->addDefinition($d);
            $v = $real->version($d->id, $d->version);
            if ($v !== null) {
                $s->addVersion($v);
            }
        }
        foreach ($real->triggers() as $t) {
            if ($t->type === 'lifecycle' && $t->definitionId !== $definition->id) {
                $s->replaceTriggers($t->definitionId, [$t]);
            }
        }
        $now = $this->now();
        $record = new DefinitionRecord;
        $record->id = $definition->id;
        $record->key = $definition->key;
        $record->kind = $definition->kind;
        $record->title = $definition->title;
        $record->entity = $definition->entity;
        $record->status = DefinitionStatus::PUBLISHED;
        $record->version = 1;
        $record->draftJson = $json;
        $record->hasDraftChanges = false;
        $record->createdAt = $record->updatedAt = $now;
        $s->addDefinition($record);
        $v = new DefinitionVersionRecord;
        $v->definitionId = $definition->id;
        $v->version = 1;
        $v->key = $definition->key;
        $v->json = $json;
        $v->hash = self::hash($json);
        $v->publishedAt = $now;
        $s->addVersion($v);
        $s->replaceTriggers($definition->id, self::triggerRows($definition, $definition->id, $definition->key, 1, Instant::MIN));
        $s->commit();
    }

    /** Starts the definition under test: a lifecycle record (optionally applying a transition), or an automation's trigger. */
    private static function startSimulation(WorkflowEngine $sim, InMemoryStore $store, Uow $u, WorkflowDefinition $definition, SimulationRequest $request, WorkflowActor $actor): void
    {
        $c = $sim->compiled($u, $definition->id, 1);
        if ($definition->isLifecycle()) {
            if ($request->entityId === null) {
                throw new WorkflowException('noRecord', 'Pick a record to test the lifecycle with.');
            }
            $from = $request->fromState === null ? null : $c->stateByKey($request->fromState);
            if ($from !== null && $u->s instanceof InMemorySession) {
                $values = $u->s->entities()->get($definition->entity, $request->entityId)?->values ?? new stdClass;
                $values->{$c->lifecycle()->field} = $from->key;
                $u->s->put('records', "{$definition->entity}:{$request->entityId}", new StoredRecord($definition->entity, $request->entityId, 1, Values::serialize($values)));
            }
            $sim->ensureRecordInstance($u, $c, $definition->entity, $request->entityId, $actor);
            if ($request->transition !== null) {
                $sim->applyTransitionCore($u, $definition->key, $definition->entity, $request->entityId, $request->transition, $request->input ?? new stdClass, $actor, false, false, 0);
            }

            return;
        }
        $t = $definition->trigger;
        $data = $request->trigger ?? new stdClass;
        $info = new TriggerInfo($t->type, $t->field, $data->from ?? null, $data->to ?? ($t->to === null || $t->to === [] ? null : Json::normalize($t->to[0])),
            is_string($data->transition ?? null) ? $data->transition : null, $actor->type, $actor->id, $actor->name, Instant::format($u->now), self::newId(),
            ($data->data ?? null) instanceof stdClass ? $data->data : null);
        $entity = $t->type === TriggerTypes::SCHEDULE && $t->forEach === null ? null : $definition->entity;
        $sim->startAutomation($u, $c, $entity, $entity === null ? null : $request->entityId, $info, $actor, 0, $request->input, null, null, null);
    }

    /**
     * What a test run shows: its instances, open tasks, the record and the log, masked like the audit log; fields
     * workflows may not read are left out.
     *
     * @param  list<LintProblem>  $problems
     */
    private function collectSimulation(WorkflowEngine $sim, InMemoryStore $store, SimulationLog $log, WorkflowDefinition $definition, SimulationRequest $request, array $problems,
        ?string $error): SimulationResult
    {
        [$instances, $tasks] = $sim->unit(function (Uow $u) use ($sim) {
            $found = $u->s->queryInstances(new InstanceQuery(take: 1000));
            usort($found, fn ($a, $b) => $a->startedAt <=> $b->startedAt);

            return [array_map(fn ($i) => $sim->instanceView($u, $i->id), $found),
                array_map(fn ($t) => $sim->taskView($u, $t, null), $u->s->queryTasks(new TaskQuery(statuses: [TaskStatus::OPEN], take: 1000)))];
        });
        $view = $sim->open($store->begin());
        $mask = $view->masker($definition->entity, $request->entityId);
        $record = null;
        $current = $definition->entity === null || $request->entityId === null ? null : $view->s->entities()->get($definition->entity, $request->entityId)?->values;
        if ($current !== null) {
            $readable = array_filter(Values::members($current), fn ($k) => $this->registry->field($definition->entity, (string) $k)?->isReadable() !== false, ARRAY_FILTER_USE_KEY);
            $record = $mask->mask(Values::object($readable));
        }
        $entries = array_map(fn (SimulationEntry $e) => new SimulationEntry($e->kind, $e->action, $e->subject, $mask->maskText($e->text), $mask->mask($e->data), $e->at), $log->entries);

        return new SimulationResult($problems, $entries, $instances, $tasks, $record, $error);
    }

    private static function drain(WorkflowEngine $sim): void
    {
        for ($i = 0; $i < 50; $i++) {
            if ($sim->processOutbox(100) + $sim->processTimers(100) === 0) {
                return;
            }
        }
    }

    private static function applySimulationEvent(WorkflowEngine $sim, InMemoryStore $store, ManualClock $clock, WorkflowDefinition $definition, SimulationRequest $request,
        SimulationEvent $event): void
    {
        switch ($event->type) {
            case 'advance':
                $clock->advance(IsoDuration::parse($event->duration ?? 'PT1H')->approximateMs());
                $sim->options->simulationLog?->add('clock', 'advanced', null, $event->duration);
                break;
            case 'decide':
                $sim->unit(function (Uow $u) use ($sim, $event) {
                    $task = array_find($u->s->queryTasks(new TaskQuery(statuses: [TaskStatus::OPEN], take: 1000)), fn ($t) => $event->step === null || $t->nodeId === $event->step)
                        ?? throw new WorkflowException('noTask', 'There is no open task to decide.');
                    $decided = array_map(fn ($d) => $d->userId, self::parseDecisions($task));
                    $userId = $event->user ?? array_find(array_map(Values::text(...), Values::fromJson($task->assignees) ?? []), fn ($a) => ! in_array($a, $decided, true))
                        ?? throw new WorkflowException('noTask', 'Everyone has decided this task.');
                    $user = $sim->options->identity->user($userId);
                    $sim->decideCore($u, $task->id, WorkflowActor::user($userId, $user->name ?? $userId), $event->outcome ?? 'approved', $event->comment ?? 'Test run', $event->data);
                });
                break;
            case 'signal':
                $sim->signal($event->signal ?? '', $definition->entity, $request->entityId, $event->data);
                break;
            case 'set':
                if ($definition->entity !== null && $request->entityId !== null && $event->values !== null) {
                    $store->changeRecord($definition->entity, $request->entityId, $event->values);
                }
                break;
        }
    }

    private static function putRecordDirect(InMemoryStore $store, string $entity, string $id, stdClass $values): void
    {
        $s = $store->begin();
        $s->put('records', "{$entity}:{$id}", new StoredRecord($entity, $id, 1, Values::serialize($values)));
        $s->commit();
    }
}
