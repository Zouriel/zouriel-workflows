<?php

namespace Zouriel\Workflows\Core\Model;

use stdClass;
use Zouriel\Workflows\Core\Json\Read;

/** Ends the run as `completed` or `failed`. Port of .NET `EndStep`. */
final class EndStep extends Step
{
    public string $result = 'completed';

    public ?Template $reason = null;

    protected function read(stdClass $value, string $path): void
    {
        $this->result = Read::string($value, 'result', $path, 'completed');
        $this->reason = Template::fromJson(Read::value($value, 'reason'), "{$path}.reason");
    }

    protected function members(): array
    {
        return ['result' => $this->result, 'reason' => $this->reason?->toJson()];
    }
}
