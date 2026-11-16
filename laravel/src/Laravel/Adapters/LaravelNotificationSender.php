<?php

namespace Zouriel\Workflows\Laravel\Adapters;

use Illuminate\Contracts\Cache\Repository;
use Illuminate\Contracts\Notifications\Dispatcher;
use Zouriel\Workflows\Core\Host\NotificationSender;
use Zouriel\Workflows\Core\Host\WorkflowNotification;

/**
 * Delivers workflow notifications through Laravel Notifications (`WorkflowMessage`), to the notifiables a
 * `NotifiableResolver` finds. The outbox may deliver a notification again after a failure, so each recipient gets one
 * idempotency key at most once a day (remembered in the cache).
 */
final class LaravelNotificationSender implements NotificationSender
{
    public function __construct(private readonly Dispatcher $notifications, private readonly NotifiableResolver $recipients, private readonly Repository $cache) {}

    public function send(WorkflowNotification $notification): void
    {
        foreach ($notification->to as $recipient) {
            $notifiable = $this->recipients->notifiable($recipient);
            $once = 'workflows:notified:'.sha1($notification->idempotencyKey.'|'.($recipient->userId ?? $recipient->email ?? ''));
            if ($notifiable === null || $this->cache->has($once)) {
                continue;
            }
            $this->notifications->sendNow($notifiable, new WorkflowMessage($notification));
            $this->cache->put($once, true, 86400);
        }
    }
}
