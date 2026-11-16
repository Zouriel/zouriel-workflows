<?php

namespace Zouriel\Workflows\Core\Settings;

use stdClass;
use Zouriel\Workflows\Core\Json\Read;
use Zouriel\Workflows\Core\Json\Write;
use Zouriel\Workflows\Core\Model\RetryPolicy;

/** Defaults steps and triggers use when they set none. Port of .NET `DefaultSettings`. */
final class DefaultSettings
{
    public ?RetryPolicy $retry = null;

    /** @var list<string>|null reminder offsets as ISO durations */
    public ?array $reminders = null;

    /** `perRecordSerial`, `parallel` or `skipIfRunning`. */
    public ?string $concurrency = null;

    /** Loop protection limit (default 5). */
    public ?int $maxChainDepth = null;

    /** Most items a `forEach` covers (default 500). */
    public ?int $forEachMax = null;

    public static function fromJson(stdClass $value, string $path): self
    {
        $defaults = new self;
        $defaults->retry = Read::model($value, 'retry', $path, 'Zouriel.Workflows.Model.RetryPolicy', RetryPolicy::fromJson(...));
        $defaults->reminders = Read::strings($value, 'reminders', $path);
        $defaults->concurrency = Read::optionalString($value, 'concurrency', $path);
        $defaults->maxChainDepth = Read::optionalInt($value, 'maxChainDepth', $path);
        $defaults->forEachMax = Read::optionalInt($value, 'forEachMax', $path);

        return $defaults;
    }

    public function toJson(): stdClass
    {
        return Write::object([
            'retry' => $this->retry?->toJson(), 'reminders' => $this->reminders, 'concurrency' => $this->concurrency,
            'maxChainDepth' => $this->maxChainDepth, 'forEachMax' => $this->forEachMax,
        ]);
    }
}
