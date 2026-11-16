<?php

namespace Zouriel\Workflows\Core\Host;

/** Calls webhooks; throws `WebhookFailedException` when there is no answer. Port of .NET `IWebhookClient`. */
interface WebhookClient
{
    public function send(WebhookRequest $request): WebhookResponse;
}
