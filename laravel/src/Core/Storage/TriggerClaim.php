<?php

namespace Zouriel\Workflows\Core\Storage;

/** That a change event started (or was considered by) a definition's trigger: `DEFINITION:EVENT`. */
final class TriggerClaim
{
    public function __construct(public string $key, public int $claimedAt) {}
}
