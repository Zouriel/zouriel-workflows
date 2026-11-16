<?php

namespace Zouriel\Workflows\Core\Model;

use stdClass;
use Zouriel\Workflows\Core\Json\Read;
use Zouriel\Workflows\Core\Json\Write;

/** Writes settable fields of the record (or a related one) in one committed change. Port of .NET `SetFieldsStep`. */
final class SetFieldsStep extends Step
{
    /** `self`/null for the workflow's record, or a token path to a relation. */
    public ?string $target = null;

    /** @var list<FieldValue> */
    public array $values = [];

    protected function read(stdClass $value, string $path): void
    {
        $this->target = Read::optionalString($value, 'target', $path);
        $this->values = Read::list($value, 'values', $path, FieldValue::fromJson(...)) ?? [];
    }

    protected function members(): array
    {
        return ['target' => $this->target, 'values' => Write::list($this->values)];
    }
}
