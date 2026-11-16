<?php

namespace Zouriel\Workflows\Core\Host;

/**
 * One webhook call (spec §7 `webhook`). The address was checked against the allowed origins or the
 * connection; `idempotencyKey` is sent as `Idempotency-Key` and is the same for every attempt of one
 * run of a step. `timeoutMs` limits the call. Port of .NET `WebhookRequest`.
 */
final readonly class WebhookRequest
{
    /** @param  array<string, string>  $headers */
    public function __construct(
        public string $method,
        public string $url,
        public array $headers,
        public ?string $body,
        public string $idempotencyKey,
        public ?int $timeoutMs,
    ) {}
}
