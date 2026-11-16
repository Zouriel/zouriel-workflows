<?php

namespace Zouriel\Workflows\Core\Testing;

use Zouriel\Workflows\Core\Host\NotificationSender;
use Zouriel\Workflows\Core\Host\WorkflowNotification;

/** Records delivered notifications instead of sending them; idempotent on the notification key, as real senders should be. */
final class RecordingNotifications implements NotificationSender
{
    /** @var list<WorkflowNotification> */
    public array $sent = [];

    /** @var array<string, true> */
    private array $keys = [];

    public function send(WorkflowNotification $notification): void
    {
        if (! isset($this->keys[$notification->idempotencyKey])) {
            $this->keys[$notification->idempotencyKey] = true;
            $this->sent[] = $notification;
        }
    }
}
