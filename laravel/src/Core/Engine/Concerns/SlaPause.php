<?php

namespace Zouriel\Workflows\Core\Engine\Concerns;

use Zouriel\Workflows\Core\Audit\AuditEntry;
use Zouriel\Workflows\Core\Engine\CompiledDefinition;
use Zouriel\Workflows\Core\Engine\SlaClock;
use Zouriel\Workflows\Core\Engine\SlaClocks;
use Zouriel\Workflows\Core\Engine\Uow;
use Zouriel\Workflows\Core\Host\WorkflowActor;
use Zouriel\Workflows\Core\Model\Sla;
use Zouriel\Workflows\Core\Model\State;
use Zouriel\Workflows\Core\Storage\BookmarkPurposes;
use Zouriel\Workflows\Core\Storage\BookmarkRecord;
use Zouriel\Workflows\Core\Storage\DefinitionStatus;
use Zouriel\Workflows\Core\Storage\InstanceKinds;
use Zouriel\Workflows\Core\Storage\InstanceRecord;
use Zouriel\Workflows\Core\Storage\InstanceStatus;
use Zouriel\Workflows\Core\Time\Instant;

/** SLA clock pause and resume (spec §6 `sla.pauseOnStatus`, `pauseOnEvent`, `resumeOnEvent`). Port of .NET `Engine/WorkflowEngine.SlaPause.cs`. */
trait SlaPause
{
    private static function usesPause(Sla $sla): bool
    {
        return ($sla->pauseOnStatus ?? []) !== [] || ($sla->pauseOnEvent ?? []) !== [] || ($sla->resumeOnEvent ?? []) !== [];
    }

    private static function isWorkingSla(Uow $u, CompiledDefinition $c, Sla $sla): bool
    {
        return ($sla->calendar ?? $c->definition->settings?->calendar) === 'working' && $u->calendar->hasWorkingTime;
    }

    private static function slaAudit(CompiledDefinition $c, InstanceRecord $record, string $action, ?WorkflowActor $actor, array $data): AuditEntry
    {
        return new AuditEntry('lifecycle', $action, 'record', $record->entityId, $actor, $record->entity, $record->entityId, $record->id, $c->definitionId, data: (object) $data,
            correlationId: $record->correlationId);
    }

    private function slaTimer(Uow $u, InstanceRecord $record, State $state): ?BookmarkRecord
    {
        return array_find($u->s->bookmarks($record->id), fn (BookmarkRecord $b) => $b->purpose === BookmarkPurposes::SLA_PREFIX.$state->id);
    }

    /**
     * Before a record moves from `$from` to `$to` (the from-state's timers are still there): keeps the from-state's clock
     * when `$to` is one of its `pauseOnStatus`, carries kept clocks of other states that still apply, and drops the rest.
     * Returns the audit entries to write once the move itself is audited. Does not save the instance.
     *
     * @return list<AuditEntry>
     */
    private function prepareSlaCarry(Uow $u, CompiledDefinition $c, InstanceRecord $record, State $from, State $to, WorkflowActor $actor): array
    {
        $audits = [];
        $clocks = SlaClocks::read($record);
        $next = [];
        $own = $from->sla;
        if (! $to->isTerminal() && $from->id !== $to->id && ! $from->isTerminal() && $own !== null && ! isset($clocks[$from->id])
            && in_array($to->id, $own->pauseOnStatus ?? [], true)) {
            $timer = $this->slaTimer($u, $record, $from);
            if ($timer === null) {
                $next[$from->id] = new SlaClock(reasons: [SlaClock::STATUS], ran: true);
            } else {
                $working = self::isWorkingSla($u, $c, $own);
                $left = $u->calendar->workingTimeBetween($u->now, $timer->dueAt ?? $u->now, $working);
                $next[$from->id] = new SlaClock($left, $working, [SlaClock::STATUS]);
                $audits[] = self::slaAudit($c, $record, 'slaPaused', $actor, ['state' => $from->key, 'reason' => SlaClock::STATUS, 'status' => $to->key]);
            }
        }
        foreach ($clocks as $id => $k) {
            $id = (string) $id;
            $owner = $c->stateById($id);
            if ($owner?->sla === null || $to->isTerminal() || ($to->id === $id && $from->id === $id)) {
                continue;
            }
            if ($to->id === $id) {
                $next[$id] = $k;
            } elseif (in_array($to->id, $owner->sla->pauseOnStatus ?? [], true)) {
                $k->addReason(SlaClock::STATUS);
                $next[$id] = $k;
            }
        }
        if ($next !== [] || $clocks !== []) {
            SlaClocks::write($record, $next);
        }

        return $audits;
    }

    /**
     * Plans the SLA timer of a state the record is in, taking a kept clock into account. True when a kept clock decided
     * it (the caller plans nothing more); false when there is none (a fresh clock starts, as always).
     */
    private function planKeptSla(Uow $u, CompiledDefinition $c, InstanceRecord $record, State $state, ?WorkflowActor $actor, string $reason): bool
    {
        if (strlen($record->data) <= 2) {
            return false;
        }
        $clocks = SlaClocks::read($record);
        $k = $clocks[$state->id] ?? null;
        if ($k === null) {
            return false;
        }
        if ($k->ran) {
            unset($clocks[$state->id]);
        } elseif ($k->due !== null) {
            $u->s->addBookmark($this->recordTimer($u, $record, BookmarkPurposes::SLA_PREFIX.$state->id, $k->due));
            unset($clocks[$state->id]);
        } else {
            $k->removeReason(SlaClock::STATUS);
            if ($k->reasons === []) {
                $at = $u->calendar->addTime($u->now, $k->leftMs, $k->working);
                $u->s->addBookmark($this->recordTimer($u, $record, BookmarkPurposes::SLA_PREFIX.$state->id, $at));
                unset($clocks[$state->id]);
                $u->audit(self::slaAudit($c, $record, 'slaResumed', $actor, ['state' => $state->key, 'reason' => $reason, 'due' => Instant::format($at)]));
            }
        }
        SlaClocks::write($record, $clocks);
        $u->s->updateInstance($record);

        return true;
    }

    /** A signal about a record pauses or resumes the clock of the state it is in (spec §6 `sla.pauseOnEvent` / `resumeOnEvent`). */
    private function applySlaSignal(Uow $u, string $signal, string $entity, string $entityId, ?WorkflowActor $actor): void
    {
        foreach ($this->liveRecordInstances($u, $entity, $entityId) as $record) {
            if ($record->kind !== InstanceKinds::RECORD || ! in_array($record->status, [InstanceStatus::ACTIVE, InstanceStatus::INCIDENT], true) || $record->state === null) {
                continue;
            }
            $definition = $u->s->definition($record->definitionId);
            if ($definition === null || $definition->status === DefinitionStatus::ARCHIVED) {
                continue;
            }
            $c = $this->compiled($u, $record->definitionId, $record->version);
            $state = $c->stateById($record->state);
            if ($state === null || $state->isTerminal() || $state->sla === null) {
                continue;
            }
            $sla = $state->sla;
            $pauses = in_array($signal, $sla->pauseOnEvent ?? [], true);
            $resumes = in_array($signal, $sla->resumeOnEvent ?? [], true);
            if (! $pauses && ! $resumes) {
                continue;
            }
            $clocks = SlaClocks::read($record);
            $k = $clocks[$state->id] ?? null;
            if ($k !== null && ($k->ran || $k->due !== null)) {
                continue;
            }
            if ($resumes && $k !== null && $k->hasReason(SlaClock::EVENT)) {
                $k->removeReason(SlaClock::EVENT);
                if ($k->reasons === []) {
                    $at = $u->calendar->addTime($u->now, $k->leftMs, $k->working);
                    $u->s->addBookmark($this->recordTimer($u, $record, BookmarkPurposes::SLA_PREFIX.$state->id, $at));
                    unset($clocks[$state->id]);
                    $u->audit(self::slaAudit($c, $record, 'slaResumed', $actor, ['state' => $state->key, 'reason' => SlaClock::EVENT, 'due' => Instant::format($at)]));
                }
            } elseif ($pauses && $k !== null) {
                if ($k->hasReason(SlaClock::EVENT)) {
                    continue;
                }
                $k->addReason(SlaClock::EVENT);
            } elseif ($pauses) {
                $timer = $this->slaTimer($u, $record, $state);
                if ($timer === null) {
                    continue;
                }
                $working = self::isWorkingSla($u, $c, $sla);
                $left = $u->calendar->workingTimeBetween($u->now, $timer->dueAt ?? $u->now, $working);
                $u->s->removeBookmark($timer->id);
                $clocks[$state->id] = new SlaClock($left, $working, [SlaClock::EVENT]);
                if ($record->status === InstanceStatus::INCIDENT && self::isParked($timer)) {
                    self::movedOutOfIncident($record);
                    $record->status = InstanceStatus::ACTIVE;
                }
                $u->audit(self::slaAudit($c, $record, 'slaPaused', $actor, ['state' => $state->key, 'reason' => SlaClock::EVENT, 'signal' => $signal]));
            } else {
                continue;
            }
            SlaClocks::write($record, $clocks);
            $record->updatedAt = $u->now;
            $u->s->updateInstance($record);
        }
    }
}
