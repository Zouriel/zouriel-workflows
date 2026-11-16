<?php

namespace Zouriel\Workflows\Laravel\Adapters;

use Illuminate\Notifications\Messages\MailMessage;
use Illuminate\Notifications\Notification;
use Zouriel\Workflows\Core\Host\WorkflowNotification;

/** A workflow notification as a Laravel notification: mail for `email`, the database channel for `inApp`, any other channel by name. */
final class WorkflowMessage extends Notification
{
    public function __construct(public readonly WorkflowNotification $notification) {}

    /** @return list<string> */
    public function via(object $notifiable): array
    {
        return [match ($this->notification->channel) {
            'email' => 'mail',
            'inApp' => 'database',
            default => $this->notification->channel,
        }];
    }

    public function toMail(object $notifiable): MailMessage
    {
        $mail = (new MailMessage)->subject($this->notification->subject ?? '')->line($this->notification->body);

        return $this->notification->link === null ? $mail : $mail->action('Open', $this->notification->link);
    }

    /** @return array<string, mixed> */
    public function toArray(object $notifiable): array
    {
        return [
            'subject' => $this->notification->subject, 'body' => $this->notification->body, 'link' => $this->notification->link,
            'instanceId' => $this->notification->instanceId, 'entity' => $this->notification->entity, 'entityId' => $this->notification->entityId,
        ];
    }
}
