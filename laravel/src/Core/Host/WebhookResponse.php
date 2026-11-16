<?php

namespace Zouriel\Workflows\Core\Host;

/** A webhook's answer: the status (redirects are returned, not followed) and the body as text. */
final readonly class WebhookResponse
{
    public function __construct(public int $status, public ?string $body) {}
}
