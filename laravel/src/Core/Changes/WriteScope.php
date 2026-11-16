<?php

namespace Zouriel\Workflows\Core\Changes;

use Zouriel\Workflows\Core\Host\WorkflowActor;

/**
 * Context for record writes made inside the engine, so the change hook knows they came from a workflow:
 * the actor, the loop protection depth (spec §5), the instance and definition (no self-retrigger), the
 * owned fields it may write, and whether the writes start no automations (a reset's undo). Port of
 * .NET `Changes/EntityChange.cs` `WorkflowWriteScope`.
 */
final class WriteScope
{
    /** @var array<string, true> "entity:id:field" entries the engine may write although a lifecycle owns them */
    private array $ownedWrites = [];

    public function __construct(
        public readonly WorkflowActor $actor,
        public readonly int $depth,
        public readonly ?string $instanceId = null,
        public readonly ?string $definitionId = null,
        public readonly ?string $correlationId = null,
        public readonly bool $suppressTriggers = false,
    ) {}

    /** Lets this scope write a lifecycle-owned field of one record (the engine applying a transition). */
    public function allowOwned(string $entity, string $id, string $field): void
    {
        $this->ownedWrites["{$entity}:{$id}:{$field}"] = true;
    }

    public function mayWriteOwned(string $entity, string $id, string $field): bool
    {
        return isset($this->ownedWrites["{$entity}:{$id}:{$field}"]);
    }
}
