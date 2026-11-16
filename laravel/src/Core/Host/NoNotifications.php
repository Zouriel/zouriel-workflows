<?php

namespace Zouriel\Workflows\Core\Host;

/** The default sender: drops notifications. */
final class NoNotifications implements NotificationSender
{
    public function send(WorkflowNotification $notification): void {}
}
