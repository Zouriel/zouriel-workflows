<?php

namespace Zouriel\Workflows\Core\Audit;

use Closure;
use Zouriel\Workflows\Core\Json\Values;
use Zouriel\Workflows\Core\Storage\AuditHead;
use Zouriel\Workflows\Core\Storage\WorkflowSession;
use Zouriel\Workflows\Core\Time\Instant;

/**
 * Collects the audit entries of one unit of work and chains them right before it commits; the chain head
 * is locked only for that last moment. Port of .NET `Audit/AuditWriter.cs`.
 */
final class AuditWriter
{
    /** @var list<AuditRecord> */
    private array $pending = [];

    /** @param  Closure(): int  $now */
    public function __construct(private readonly WorkflowSession $session, private readonly ?string $tenant, private readonly Closure $now) {}

    /** Queues an entry; its sequence number and hash are assigned when the unit commits. */
    public function write(AuditEntry $e): void
    {
        $this->pending[] = new AuditRecord(
            id: bin2hex(random_bytes(16)),
            at: Instant::format(($this->now)()),
            actorType: $e->actor->type,
            category: $e->category,
            action: $e->action,
            subjectType: $e->subjectType,
            subjectId: $e->subjectId,
            correlationId: $e->correlationId ?? $e->instanceId ?? $e->subjectId,
            actorId: $e->actor->id,
            actorName: $e->actor->name,
            tenant: $this->tenant,
            entity: $e->entity,
            entityId: $e->entityId,
            instanceId: $e->instanceId,
            definitionId: $e->definitionId,
            before: $e->before === null ? null : Values::serialize($e->before),
            after: $e->after === null ? null : Values::serialize($e->after),
            data: $e->data === null ? null : Values::serialize($e->data),
            causationId: $e->causationId,
        );
    }

    /** Locks the chain head, links the queued entries to it, stores them and moves the head. */
    public function flush(): void
    {
        if ($this->pending === []) {
            return;
        }
        $head = $this->session->auditHead($this->tenant);
        $seq = $head->seq ?? 0;
        $previous = $head->hash ?? AuditChain::GENESIS_HASH;
        foreach ($this->pending as $record) {
            $record->seq = ++$seq;
            $record->prevHash = $previous;
            $record->hash = AuditChain::computeHash($record);
            $previous = $record->hash;
            $this->session->addAudit($record);
        }
        $this->session->setAuditHead($this->tenant, new AuditHead($seq, $previous));
        $this->pending = [];
    }
}
