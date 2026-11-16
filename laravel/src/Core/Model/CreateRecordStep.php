<?php

namespace Zouriel\Workflows\Core\Model;

use stdClass;
use Zouriel\Workflows\Core\Json\Read;
use Zouriel\Workflows\Core\Json\Write;

/** Creates a record (spec §7 createRecord); its id is the output `id`. Port of .NET `CreateRecordStep`. */
final class CreateRecordStep extends Step
{
    /** The registry entity key of the record to create. */
    public string $entity = '';

    /** @var list<FieldValue> */
    public array $values = [];

    protected function read(stdClass $value, string $path): void
    {
        $this->entity = Read::string($value, 'entity', $path);
        $this->values = Read::list($value, 'values', $path, FieldValue::fromJson(...)) ?? [];
    }

    protected function members(): array
    {
        return ['entity' => $this->entity, 'values' => Write::list($this->values)];
    }
}
