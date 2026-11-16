<?php

namespace Zouriel\Workflows\Core\Storage;

/** The newest entry of an audit chain: its sequence number and hash. */
final readonly class AuditHead
{
    public function __construct(public int $seq, public string $hash) {}
}
