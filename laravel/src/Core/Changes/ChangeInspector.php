<?php

namespace Zouriel\Workflows\Core\Changes;

use stdClass;
use Zouriel\Workflows\Core\Engine\WorkflowEngine;
use Zouriel\Workflows\Core\Json\Values;
use Zouriel\Workflows\Core\Registry\FieldTypes;
use Zouriel\Workflows\Core\Storage\OutboxKinds;
use Zouriel\Workflows\Core\Storage\OutboxRecord;

/**
 * Turns a record write into a change event (spec §5) and enforces lifecycle field ownership (spec §6).
 * Called by gateways (and the Eloquent hook) before the write commits. Port of .NET `Changes/ChangeInspector.cs`.
 */
final class ChangeInspector
{
    public function __construct(private readonly WorkflowEngine $engine) {}

    /**
     * @param  stdClass|null  $before  registered field values before the write (null for creates)
     * @param  stdClass|null  $after  registered field values after the write (null for deletes)
     */
    public function inspect(string $entity, string $id, string $kind, ?stdClass $before, ?stdClass $after): InspectResult
    {
        $descriptor = $this->engine->registry->entity($entity);
        if ($descriptor === null) {
            return new InspectResult;
        }
        $scope = $this->engine->scopes->current();
        $ownership = $this->engine->ownership();
        // Which lifecycle controls a field depends on the record (spec §6 applies to): the one it is pinned to; for a
        // record with no lifecycle yet, a new record's own values decide, an existing record's values before this write do.
        $recordValues = $kind === ChangeKinds::CREATED ? $after : $before;
        $today = $ownership->calendar->today($this->engine->now());
        $initial = [];
        $fields = [];
        $engineFields = [];
        foreach ($descriptor->fields as $f) {
            if (! FieldTypes::isScalar($f->type) && $f->type !== FieldTypes::RELATION) {
                continue;
            }
            $from = $before === null ? null : Values::normalize($before->{$f->key} ?? null);
            $to = $after === null ? null : Values::normalize($after->{$f->key} ?? null);
            $lifecycle = $ownership->for($entity, $f->key, $id, $recordValues, $today);
            if ($kind === ChangeKinds::CREATED && $lifecycle !== null && Values::isEmpty($to) && $lifecycle->initialState !== null) {
                $initial[$f->key] = $lifecycle->initialState;
                $to = $lifecycle->initialState;
                $engineFields[] = $f->key;
            }
            if ($kind === ChangeKinds::UPDATED && Values::deepEquals($from, $to)) {
                continue;
            }
            if ($kind !== ChangeKinds::DELETED) {
                $fields[$f->key] = new FieldChange($from, $to);
            }
            if ($kind === ChangeKinds::UPDATED && $lifecycle !== null) {
                if ($scope?->mayWriteOwned($entity, $id, $f->key) === true) {
                    $engineFields[] = $f->key;
                } elseif ($lifecycle->enforce) {
                    $this->engine->recordRefusedWrite($entity, $id, $f->key, $lifecycle->definitionId, $from, $to, $scope);
                    throw new FieldOwnedException($entity, $f->key, $lifecycle->definitionKey);
                }
            }
        }
        if ($kind === ChangeKinds::UPDATED && $fields === []) {
            return new InspectResult(null, $initial);
        }
        $actor = $scope?->actor ?? $this->engine->currentActor();
        $change = new EntityChange;
        $change->kind = $kind;
        $change->entity = $entity;
        $change->entityId = $id;
        $change->fields = $kind === ChangeKinds::DELETED ? [] : $fields;
        $change->snapshot = $kind === ChangeKinds::DELETED && $before !== null ? Values::normalize($before) : null;
        $change->actorType = $actor?->type ?? 'system';
        $change->actorId = $actor?->id;
        $change->actorName = $actor?->name;
        $change->depth = $scope?->depth ?? 0;
        $change->causationInstanceId = $scope?->instanceId;
        $change->causationDefinitionId = $scope?->definitionId;
        $change->engineFields = $engineFields === [] ? null : $engineFields;
        $change->correlationId = $scope?->correlationId;
        $change->at = $this->engine->now();
        $change->suppressTriggers = $scope?->suppressTriggers === true;

        return new InspectResult($change, $initial);
    }

    /** The outbox row for a change event. */
    public function toOutbox(EntityChange $change): OutboxRecord
    {
        $row = new OutboxRecord;
        $row->id = OutboxRecord::newId($change->at);
        $row->kind = OutboxKinds::CHANGE;
        $row->payload = $change->toJson();
        $row->createdAt = $change->at;
        $row->recordKey = OutboxRecord::keyOf($change->entity, $change->entityId);

        return $row;
    }
}
