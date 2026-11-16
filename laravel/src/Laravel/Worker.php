<?php

namespace Zouriel\Workflows\Laravel;

use Zouriel\Workflows\Core\Engine\WorkflowEngine;

/**
 * One round of background work, as .NET's hosted `WorkflowWorker` does it: audits of refused writes, committed change
 * and lifecycle events and queued notifications (the outbox), due timers, and now and then a cleanup of old outbox
 * rows and trigger claims. Rows are claimed with a lease, so several workers can run at once.
 */
final class Worker
{
    private ?int $lastCleanup = null;

    public function __construct(
        private readonly WorkflowEngine $engine,
        private readonly int $batch = 100,
        private readonly int $cleanupEverySeconds = 3600,
        private readonly int $keepOutboxDays = 7,
        private readonly int $keepClaimsDays = 30,
    ) {}

    /** Runs one round and returns how many outbox rows and timers it took (0 = nothing was due). */
    public function round(): int
    {
        $this->engine->flushBackground();
        $handled = $this->engine->processOutbox($this->batch) + $this->engine->processTimers($this->batch);
        $this->engine->flushBackground();
        if ($this->lastCleanup === null || time() - $this->lastCleanup >= $this->cleanupEverySeconds) {
            $this->engine->cleanup($this->keepOutboxDays * 86400000, $this->keepClaimsDays * 86400000);
            $this->lastCleanup = time();
        }

        return $handled;
    }
}
