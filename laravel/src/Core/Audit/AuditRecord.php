<?php

namespace Zouriel\Workflows\Core\Audit;

/**
 * One stored audit entry (spec §11), as .NET's `Storage/Records.cs` AuditRecord.
 * `before`, `after` and `data` are JSON text exactly as written, so a re-read hashes the same.
 */
final class AuditRecord
{
    public function __construct(
        public string $id,
        public string $at,
        public string $actorType,
        public string $category,
        public string $action,
        public string $subjectType,
        public string $subjectId,
        public string $correlationId,
        public ?string $actorId = null,
        public ?string $actorName = null,
        public ?string $tenant = null,
        public ?string $entity = null,
        public ?string $entityId = null,
        public ?string $instanceId = null,
        public ?string $definitionId = null,
        public ?string $before = null,
        public ?string $after = null,
        public ?string $data = null,
        public ?string $causationId = null,
        public int $seq = 0,
        public string $prevHash = '',
        public string $hash = '',
    ) {}
}
