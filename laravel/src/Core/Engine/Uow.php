<?php

namespace Zouriel\Workflows\Core\Engine;

use Zouriel\Workflows\Core\Audit\AuditEntry;
use Zouriel\Workflows\Core\Audit\AuditWriter;
use Zouriel\Workflows\Core\Audit\Masker;
use Zouriel\Workflows\Core\Settings\GlobalSettings;
use Zouriel\Workflows\Core\Storage\InstanceRecord;
use Zouriel\Workflows\Core\Storage\WorkflowSession;
use Zouriel\Workflows\Core\Time\WorkCalendar;
use Zouriel\Workflows\Core\WorkflowException;

/**
 * One unit of work: the session, the settings it read, the calendar, the clock reading, the audit writer,
 * and every instance it holds (each instance is held once per unit, so code that reaches it twice shares
 * one copy). Port of .NET `WorkflowEngine.cs` `Uow`.
 */
final class Uow
{
    public readonly WorkCalendar $calendar;

    public readonly string $today;

    public readonly AuditWriter $auditWriter;

    /** @var list<string> instances created in this unit that still have to run (sub-workflows start after their parent is saved) */
    public array $deferred = [];

    /** @var list<InstanceRecord> instances that finished in this unit, so queued ones can start after them */
    public array $finished = [];

    /** @var array<string, Exec> */
    public array $execs = [];

    /** @var array<string, true> instances whose steps are executing right now */
    public array $driving = [];

    /** @var array<string, InstanceRecord> lifecycle record instances loaded in this unit */
    public array $records = [];

    /** @var list<array{0: string, 1: string, 2: string, 3: ?string}> records pinned (or unpinned) to a lifecycle: entity, field, id, definition */
    public array $pins = [];

    /** @var array<string, Masker> */
    private array $maskers = [];

    public function __construct(
        public readonly WorkflowEngine $engine,
        public readonly WorkflowSession $s,
        public readonly GlobalSettings $settings,
        public readonly int $now,
        public readonly bool $simulation,
        public readonly ?SimulationLog $log,
    ) {
        $warn = $engine->options->warn;
        $this->calendar = $settings->workCalendar($warn === null ? null : fn (string $m) => $warn($m));
        $this->today = $this->calendar->today($now);
        $this->auditWriter = new AuditWriter($s, $engine->options->tenant, fn () => $engine->now());
    }

    public function scope(?InstanceRecord $i = null, ?RunState $state = null, ?Token $token = null, ?TriggerInfo $trigger = null, ?string $entity = null,
        ?string $entityId = null): ValueScope
    {
        [$item, $itemIndex] = $token?->currentItem() ?? [null, null];
        $itemEntity = $item !== null && is_string($item->{'$entity'} ?? null) ? $item->{'$entity'} : null;

        return new ValueScope($this->s->entities(), $this->engine->registry, $this->now, $this->today,
            entity: $entity ?? $i?->entity,
            entityId: $entityId ?? $i?->entityId,
            entityOverride: $state?->snapshot,
            trigger: $trigger ?? ($i === null ? null : TriggerInfo::fromJson($i->triggerJson)),
            state: $state,
            item: $item,
            itemIndex: $itemIndex,
            itemEntity: $itemEntity,
            instance: $i,
            zone: $this->settings->zone(),
        );
    }

    /** Queues an audit entry (masked, spec §11); the chain is linked when the unit commits. */
    public function audit(AuditEntry $e): void
    {
        $m = $this->masker($e->entity, $e->entityId);
        $data = $m->mask($e->data);
        if ($data instanceof \stdClass && property_exists($data, 'trigger')) {
            $data->trigger = $m->maskTrigger($e->data->trigger);
        }
        $masked = clone $e;
        $masked->before = $m->mask($e->before);
        $masked->after = $m->mask($e->after);
        $masked->data = $data;
        $this->auditWriter->write($masked);
    }

    /** The masker for one record (its current sensitive values), cached for the unit. */
    public function masker(?string $entity, ?string $entityId): Masker
    {
        $key = "{$entity}\n{$entityId}";
        if (isset($this->maskers[$key])) {
            return $this->maskers[$key];
        }
        $values = null;
        if ($entity !== null && $entityId !== null && $this->engine->registry->entity($entity) !== null) {
            try {
                $values = $this->s->entities()->get($entity, $entityId)?->values;
            } catch (WorkflowException) {
                // Records the gateway cannot read are masked by key only.
            }
        }

        return $this->maskers[$key] = Masker::create($this->engine->registry, $this->settings, $values, $entity);
    }

    /** Forgets a record's cached sensitive values (after a workflow wrote to it). */
    public function forgetMasker(string $entity, string $entityId): void
    {
        unset($this->maskers["{$entity}\n{$entityId}"]);
    }
}
