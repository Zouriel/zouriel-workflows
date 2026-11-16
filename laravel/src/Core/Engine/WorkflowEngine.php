<?php

namespace Zouriel\Workflows\Core\Engine;

use Closure;
use stdClass;
use Throwable;
use Zouriel\Forms\Json;
use Zouriel\Workflows\Core\Audit\AuditEntry;
use Zouriel\Workflows\Core\Changes\ChangeInspector;
use Zouriel\Workflows\Core\Changes\WriteScope;
use Zouriel\Workflows\Core\Changes\WriteScopes;
use Zouriel\Workflows\Core\Engine\Concerns\Definitions;
use Zouriel\Workflows\Core\Engine\Concerns\Lifecycles;
use Zouriel\Workflows\Core\Engine\Concerns\Resets;
use Zouriel\Workflows\Core\Engine\Concerns\Runs;
use Zouriel\Workflows\Core\Engine\Concerns\Simulation;
use Zouriel\Workflows\Core\Engine\Concerns\SlaPause;
use Zouriel\Workflows\Core\Engine\Concerns\Steps;
use Zouriel\Workflows\Core\Engine\Concerns\Tasks;
use Zouriel\Workflows\Core\Engine\Concerns\Triggers;
use Zouriel\Workflows\Core\Engine\Concerns\Views;
use Zouriel\Workflows\Core\Host\WorkflowActor;
use Zouriel\Workflows\Core\Json\Values;
use Zouriel\Workflows\Core\Model\Definitions as DefinitionJson;
use Zouriel\Workflows\Core\Model\WorkflowKinds;
use Zouriel\Workflows\Core\Registry\WorkflowRegistry;
use Zouriel\Workflows\Core\Settings\GlobalSettings;
use Zouriel\Workflows\Core\Storage\InstanceKinds;
use Zouriel\Workflows\Core\Storage\InstanceQuery;
use Zouriel\Workflows\Core\Storage\InstanceStatus;
use Zouriel\Workflows\Core\Storage\SettingsRecord;
use Zouriel\Workflows\Core\Storage\WorkflowConcurrencyException;
use Zouriel\Workflows\Core\Storage\WorkflowSession;
use Zouriel\Workflows\Core\WorkflowException;

/**
 * The workflow engine. Every public operation is one unit of work: workflow state, audit entries, outbox
 * rows and (with a database store) the host's record changes commit together or not at all. Port of .NET
 * `Engine/WorkflowEngine.cs`; the other partials of the .NET class are the traits in `Concerns/`.
 */
final class WorkflowEngine
{
    use Definitions, Lifecycles, Resets, Runs, Simulation, SlaPause, Steps, Tasks, Triggers, Views;

    public readonly WorkflowRegistry $registry;

    /** Turns record writes into change events and enforces lifecycle field ownership (used by gateways and the Eloquent hook). */
    public readonly ChangeInspector $changes;

    /** The write scopes of the current request (who record writes made inside the engine belong to). */
    public readonly WriteScopes $scopes;

    /** How long a claimed outbox row or timer stays locked to one worker (milliseconds). */
    public int $claimLockMs = 120000;

    /** How many times a timer may fail before it is parked for an admin. */
    public int $maxTimerAttempts = 10;

    /** @var array<string, CompiledDefinition> by "definition\nversion" */
    private array $compiled = [];

    private ?OwnershipMap $ownershipMap = null;

    /** @var list<array{0: int, 1: string, 2: string, 3: string, 4: ?string}> pin changes this engine committed, numbered */
    private array $pinLog = [];

    private int $pinSeq = 0;

    /** @var list<Closure(): void> audit writes waiting for the unit that refused a write to end */
    private array $background = [];

    public function __construct(public readonly EngineOptions $options)
    {
        $this->registry = $options->registry;
        $this->scopes = new WriteScopes;
        $this->changes = new ChangeInspector($this);
    }

    public function now(): int
    {
        return $this->options->clock->now();
    }

    // ── Units of work ─────────────────────────────────────────────────────

    public function open(WorkflowSession $session): Uow
    {
        return new Uow($this, $session, GlobalSettings::parse($session->settings()?->json), $this->now(), $this->options->simulationLog !== null,
            $this->options->simulationLog);
    }

    /**
     * Runs one unit of work, retrying when a concurrent writer changed the same rows.
     *
     * @template T
     *
     * @param  callable(Uow): T  $work
     * @return T
     */
    public function unit(callable $work): mixed
    {
        $this->refreshOwnership();
        for ($attempt = 1; ; $attempt++) {
            try {
                $session = $this->options->store->begin();
                $u = $this->open($session);
                $result = $work($u);
                $this->runDeferred($u);
                $this->startQueuedAfter($u);
                $u->auditWriter->flush();
                $session->commit();
                $this->applyPins($u);

                return $result;
            } catch (WorkflowConcurrencyException $e) {
                if ($attempt >= 4) {
                    throw $e;
                }
                usleep(20000 * $attempt);
            }
        }
    }

    public function runDeferred(Uow $u): void
    {
        while ($u->deferred !== []) {
            $id = array_shift($u->deferred);
            $x = $this->loadExec($u, $id);
            if ($x->i->status === InstanceStatus::RUNNING) {
                $this->drive($x);
            }
        }
    }

    public function compiled(Uow $u, string $definitionId, int $version): CompiledDefinition
    {
        $key = "{$definitionId}\n{$version}";
        if (isset($this->compiled[$key])) {
            return $this->compiled[$key];
        }
        $v = $u->s->version($definitionId, $version)
            ?? throw new WorkflowException('definitionNotFound', "Version {$version} of workflow {$definitionId} does not exist.");

        return $this->compiled[$key] = new CompiledDefinition(DefinitionJson::parse($v->json), $definitionId, $version);
    }

    public static function newId(): string
    {
        return bin2hex(random_bytes(16));
    }

    /**
     * The order lifecycles sharing a field are tried in (spec §6 applies to): those with a condition first,
     * in publish order (first publish time, then definition id), then the fallback.
     *
     * @template T
     *
     * @param  list<T>  $items
     * @return list<T>
     */
    public static function inSelectionOrder(array $items, callable $conditional, callable $publishedAt, callable $definitionId): array
    {
        $keyed = array_map(fn ($item, $i) => [$item, $i], $items, array_keys($items));
        usort($keyed, fn ($a, $b) => [$conditional($a[0]) ? 0 : 1, $publishedAt($a[0])] <=> [$conditional($b[0]) ? 0 : 1, $publishedAt($b[0])]
            ?: strcmp($definitionId($a[0]), $definitionId($b[0])) ?: $a[1] <=> $b[1]);

        return array_column($keyed, 0);
    }

    // ── Lifecycle ownership (for the change hook) ─────────────────────────

    /** The lifecycle-owned fields as last loaded (loaded on first use). */
    public function ownership(): OwnershipMap
    {
        return $this->ownershipMap ??= $this->loadOwnership();
    }

    /** Reloads the lifecycle-owned fields when the cached copy is older than the refresh interval. */
    public function refreshOwnership(): void
    {
        $m = $this->ownershipMap;
        $now = $this->now();
        if ($m !== null && $now - $m->loadedAt < $this->options->ownershipRefreshMs && $now >= $m->loadedAt) {
            return;
        }
        $this->ownershipMap = $this->loadOwnership();
    }

    private function loadOwnership(): OwnershipMap
    {
        $seenSeq = $this->pinSeq;
        $s = $this->options->store->begin();
        $fields = [];
        foreach ($s->triggers() as $t) {
            if ($t->type !== 'lifecycle' || $t->entity === null || $t->field === null) {
                continue;
            }
            $data = Values::fromJson($t->data ?? '{}');
            $fields[] = new OwnedField($t->definitionId, $t->definitionKey, $t->entity, $t->field, ($data->mode ?? null) !== 'observe',
                is_string($data->initial ?? null) ? $data->initial : null, self::appliesWhenOf($t), $t->publishedAt);
        }
        $warn = $this->options->warn;
        $calendar = GlobalSettings::parse($s->settings()?->json)->workCalendar($warn === null ? null : fn (string $m) => $warn($m));
        // Pins matter where the lifecycle depends on the record: a field shared by several lifecycles (archived ones
        // included, their records stay pinned) or one with a condition.
        $groups = [];
        foreach ($s->definitions() as $d) {
            if ($d->kind !== WorkflowKinds::LIFECYCLE || $d->version === 0) {
                continue;
            }
            $key = "{$d->id}\n{$d->version}";
            if (! isset($this->compiled[$key])) {
                $v = $s->version($d->id, $d->version);
                if ($v === null) {
                    continue;
                }
                $this->compiled[$key] = new CompiledDefinition(DefinitionJson::parse($v->json), $d->id, $d->version);
            }
            $c = $this->compiled[$key];
            if ($c->definition->entity !== null && $c->definition->lifecycle !== null) {
                $groups["{$c->definition->entity}\n{$c->definition->lifecycle->field}"][] = $c;
            }
        }
        $pairs = [];
        $pins = [];
        foreach ($groups as $pair => $list) {
            if (count($list) < 2 && array_all($list, fn (CompiledDefinition $c) => $c->definition->lifecycle->appliesWhen === null)) {
                continue;
            }
            $pairs[$pair] = true;
            foreach ($list as $c) {
                for ($skip = 0; ; $skip += 1000) {
                    $page = $s->queryInstances(new InstanceQuery(definitionId: $c->definitionId, kinds: [InstanceKinds::RECORD],
                        statuses: [InstanceStatus::ACTIVE, InstanceStatus::INCIDENT, InstanceStatus::OUT_OF_SYNC, InstanceStatus::COMPLETED], skip: $skip, take: 1000));
                    foreach ($page as $i) {
                        if ($i->entityId !== null) {
                            $pins["{$pair}\n{$i->entityId}"] ??= $c->definitionId;
                        }
                    }
                    if (count($page) < 1000) {
                        break;
                    }
                }
            }
        }
        $map = new OwnershipMap($fields, $this->now(), $calendar, $pairs, $pins);
        foreach ($this->pinLog as [$seq, $entity, $field, $id, $definitionId]) {
            if ($seq > $seenSeq) {
                $map->pin($entity, $field, $id, $definitionId);
            }
        }
        $this->pinLog = array_values(array_filter($this->pinLog, fn ($p) => $p[0] > $seenSeq));

        return $map;
    }

    /** Applies the pins a committed unit changed to the cached ownership map. */
    private function applyPins(Uow $u): void
    {
        foreach ($u->pins as [$entity, $field, $id, $definitionId]) {
            $this->pinLog[] = [++$this->pinSeq, $entity, $field, $id, $definitionId];
            $this->ownershipMap?->pin($entity, $field, $id, $definitionId);
        }
    }

    /** Marks the lifecycle ownership cache stale; the next unit of work reloads it. */
    public function invalidateOwnership(): void
    {
        $this->ownershipMap = $this->ownershipMap?->stale();
    }

    public function currentActor(): ?WorkflowActor
    {
        return $this->options->actorAccessor?->current();
    }

    /**
     * Audits a refused write to a lifecycle-owned field in its own unit of work, once the unit that refused it
     * has ended (the refused write rolls back). `flushBackground()` writes it.
     */
    public function recordRefusedWrite(string $entity, string $id, string $field, string $definitionId, mixed $from, mixed $to, ?WriteScope $scope): void
    {
        $actor = $scope?->actor ?? $this->currentActor() ?? WorkflowActor::system();
        $this->background[] = fn () => $this->unit(fn (Uow $u) => $u->audit(new AuditEntry('lifecycle', 'writeRefused', 'record', $id, $actor,
            entity: $entity, entityId: $id, definitionId: $definitionId, before: Values::object([$field => $from]), after: Values::object([$field => $to]))));
    }

    /** Writes the audit entries left for after a refused write (tests, workers and the end of a request call it). */
    public function flushBackground(): void
    {
        while ($this->background !== []) {
            $write = array_shift($this->background);
            try {
                $write();
            } catch (Throwable $e) {
                $this->warn("Could not audit a refused write: {$e->getMessage()}");
            }
        }
    }

    public function warn(string $message): void
    {
        if ($this->options->warn !== null) {
            ($this->options->warn)($message);
        }
    }

    // ── Settings ──────────────────────────────────────────────────────────

    /** @return array{0: GlobalSettings, 1: int} the global settings (spec §12) and their revision */
    public function getSettings(): array
    {
        return $this->unit(function (Uow $u) {
            $record = $u->s->settings();

            return [GlobalSettings::parse($record?->json), $record->revision ?? 0];
        });
    }

    /**
     * Saves the global settings when `$expectedRevision` is still current and returns the new revision.
     * Refuses `invalidSettings` and `staleSettings`; audited as `settings.updated` with before and after.
     */
    public function saveSettings(GlobalSettings $settings, int $expectedRevision, WorkflowActor $actor): int
    {
        $revision = $this->unit(function (Uow $u) use ($settings, $expectedRevision, $actor) {
            $problems = $settings->problems();
            if ($problems !== []) {
                throw new WorkflowException('invalidSettings', implode(' ', $problems), $problems);
            }
            $record = $u->s->settings();
            $current = $record->revision ?? 0;
            if ($current !== $expectedRevision) {
                throw new WorkflowException('staleSettings', 'Someone else changed the settings. Reload and try again.');
            }
            $json = Values::serialize($settings->toJson());
            $saved = new SettingsRecord;
            $saved->json = $json;
            $saved->revision = $current + 1;
            $saved->updatedAt = $u->now;
            $saved->updatedBy = $actor->id;
            $u->s->saveSettings($saved);
            $u->audit(new AuditEntry('settings', 'updated', 'settings', 'global', $actor,
                before: $record === null ? null : Values::fromJson($record->json), after: Values::fromJson($json)));

            return $current + 1;
        });
        // The change hook reads "today" in the settings' time zone.
        $this->invalidateOwnership();

        return $revision;
    }

    /** A copy of an object value, or an empty object. */
    public static function obj(?stdClass $value): stdClass
    {
        return $value === null ? new stdClass : Json::copy($value);
    }
}
