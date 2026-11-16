<?php

namespace Zouriel\Workflows\Core\Model;

use stdClass;
use Zouriel\Workflows\Core\Json\Read;
use Zouriel\Workflows\Core\Json\Write;

/** Retry settings of a call-out step (spec §7). Port of .NET `RetryPolicy`. */
final class RetryPolicy
{
    /** Retries after the first attempt, 0 to 10. */
    public int $max = 0;

    /** `fixed` or `exponential`; null = fixed. */
    public ?string $backoff = null;

    /** ISO duration of the first delay (default one minute). */
    public ?string $initial = null;

    /** Cap on exponential delays; null = no cap. */
    public ?string $maxDelay = null;

    public static function fromJson(stdClass $value, string $path): self
    {
        $retry = new self;
        $retry->max = Read::int($value, 'max', $path, 0);
        $retry->backoff = Read::optionalString($value, 'backoff', $path);
        $retry->initial = Read::optionalString($value, 'initial', $path);
        $retry->maxDelay = Read::optionalString($value, 'maxDelay', $path);

        return $retry;
    }

    public function toJson(): stdClass
    {
        return Write::object(['max' => $this->max, 'backoff' => $this->backoff, 'initial' => $this->initial, 'maxDelay' => $this->maxDelay]);
    }
}
