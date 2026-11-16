<?php

namespace Zouriel\Workflows\Core\Storage;

/**
 * Filters for reading the audit log (newest first, or oldest first from `fromSeq`). `tenant` picks the
 * chain (null = the default one); stores return only that chain's entries. Port of .NET `AuditQuery`.
 */
final readonly class AuditQuery
{
    public function __construct(
        public ?string $tenant = null,
        public ?string $category = null,
        public ?string $action = null,
        public ?string $entity = null,
        public ?string $entityId = null,
        public ?string $instanceId = null,
        public ?string $definitionId = null,
        public ?string $actorId = null,
        public ?int $from = null,
        public ?int $to = null,
        public ?int $before = null,
        public ?int $fromSeq = null,
        public int $take = 100,
    ) {}

    public function withTenant(?string $tenant): self
    {
        return new self($tenant, $this->category, $this->action, $this->entity, $this->entityId, $this->instanceId, $this->definitionId,
            $this->actorId, $this->from, $this->to, $this->before, $this->fromSeq, $this->take);
    }
}
