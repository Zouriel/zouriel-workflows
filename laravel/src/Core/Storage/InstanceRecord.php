<?php

namespace Zouriel\Workflows\Core\Storage;

/**
 * One instance: an automation run, a lifecycle record instance or a child run. `liveKey` is unique
 * among instances while set: `record:DEFINITION:ENTITY:ID` for a live record instance,
 * `serial:DEFINITION:ENTITY:ID` for a running serial automation. Port of .NET `InstanceRecord`.
 */
final class InstanceRecord
{
    public string $id = '';

    public string $definitionId = '';

    public string $definitionKey = '';

    public int $version = 0;

    public string $kind = InstanceKinds::AUTOMATION;

    public ?string $parentId = null;

    public ?string $reason = null;

    public ?string $entity = null;

    public ?string $entityId = null;

    public string $status = InstanceStatus::RUNNING;

    public ?string $state = null;

    public ?int $stateEnteredAt = null;

    /** The interpreter's tokens, outputs and variables (JSON); record instances keep kept SLA clocks here. */
    public string $data = '{}';

    public ?string $triggerJson = null;

    public int $depth = 0;

    public ?string $initiatorId = null;

    public ?string $initiatorName = null;

    public ?string $error = null;

    public ?string $errorStepId = null;

    public string $correlationId = '';

    public int $startedAt = 0;

    public int $updatedAt = 0;

    public ?int $endedAt = null;

    public int $rowVersion = 0;

    public ?string $liveKey = null;

    public static function recordKey(string $definitionId, string $entity, string $id): string
    {
        return "record:{$definitionId}:{$entity}:{$id}";
    }

    public static function serialKey(string $definitionId, ?string $entity, string $id): string
    {
        return "serial:{$definitionId}:{$entity}:{$id}";
    }
}
