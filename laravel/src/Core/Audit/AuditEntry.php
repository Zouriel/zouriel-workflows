<?php

namespace Zouriel\Workflows\Core\Audit;

use Zouriel\Workflows\Core\Host\WorkflowActor;

/** An audit entry before it is chained (spec §11). `before`, `after` and `data` are workflow values. Port of .NET `Audit/AuditWriter.cs` `AuditEntry`. */
final class AuditEntry
{
    public WorkflowActor $actor;

    public function __construct(
        public string $category,
        public string $action,
        public string $subjectType,
        public string $subjectId,
        ?WorkflowActor $actor = null,
        public ?string $entity = null,
        public ?string $entityId = null,
        public ?string $instanceId = null,
        public ?string $definitionId = null,
        public mixed $before = null,
        public mixed $after = null,
        public mixed $data = null,
        public ?string $correlationId = null,
        public ?string $causationId = null,
    ) {
        $this->actor = $actor ?? WorkflowActor::system();
    }
}
