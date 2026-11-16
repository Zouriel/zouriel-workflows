<?php

namespace Zouriel\Workflows\Core\Testing;

use Zouriel\Workflows\Core\Host\WebhookClient;
use Zouriel\Workflows\Core\Host\WebhookRequest;
use Zouriel\Workflows\Core\Host\WebhookResponse;

/** Records webhook calls and answers them from canned responses per URL prefix (the last one repeats); never touches the network. */
final class FakeWebhooks implements WebhookClient
{
    /** @var list<WebhookRequest> */
    public array $calls = [];

    /** @var array<string, list<WebhookResponse>> */
    public array $responses = [];

    public function send(WebhookRequest $request): WebhookResponse
    {
        $this->calls[] = $request;
        foreach ($this->responses as $prefix => $queue) {
            if (str_starts_with($request->url, (string) $prefix) && $queue !== []) {
                return count($queue) > 1 ? array_shift($this->responses[$prefix]) : $queue[0];
            }
        }

        return new WebhookResponse(200, '{}');
    }
}
