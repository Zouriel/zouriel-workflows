<?php

namespace Zouriel\Workflows\Core\Host;

/** Sends notifications (email, in-app, SMS…). Throwing makes the outbox retry later. Port of .NET `INotificationSender`. */
interface NotificationSender
{
    public function send(WorkflowNotification $notification): void;
}
