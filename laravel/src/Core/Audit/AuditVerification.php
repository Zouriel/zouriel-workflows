<?php

namespace Zouriel\Workflows\Core\Audit;

/** The result of checking an audit chain. */
final readonly class AuditVerification
{
    /** @param  list<array{0: int, 1: int}>  $purgedGaps  runs of missing entries that recorded purges explain */
    public function __construct(
        public bool $valid,
        public int $checked,
        public ?int $brokenAtSeq,
        public ?string $problem,
        public array $purgedGaps,
    ) {}
}
