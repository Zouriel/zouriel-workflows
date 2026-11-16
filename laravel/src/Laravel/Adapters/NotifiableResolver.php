<?php

namespace Zouriel\Workflows\Laravel\Adapters;

use Zouriel\Workflows\Core\Host\NotificationRecipient;

/** Finds who receives a workflow notification in the app: a notifiable (usually the user model), or null to skip the person. */
interface NotifiableResolver
{
    public function notifiable(NotificationRecipient $recipient): ?object;
}
