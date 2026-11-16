<?php

namespace Zouriel\Workflows\Laravel\Adapters;

use Illuminate\Database\Eloquent\Model;
use Illuminate\Support\Facades\Notification;
use Zouriel\Workflows\Core\Host\NotificationRecipient;

/**
 * Recipients by user id from a user model (`config('workflows.notifications.user_model')`), and people known only by
 * email through an on-demand mail route.
 */
final class UserModelNotifiables implements NotifiableResolver
{
    /** @param  class-string<Model>|null  $userModel */
    public function __construct(private readonly ?string $userModel) {}

    public function notifiable(NotificationRecipient $recipient): ?object
    {
        if ($recipient->userId !== null && $this->userModel !== null) {
            $user = $this->userModel::query()->find($recipient->userId);
            if ($user !== null) {
                return $user;
            }
        }

        return $recipient->email === null ? null : Notification::route('mail', $recipient->email);
    }
}
