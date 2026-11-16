<?php

namespace Zouriel\Workflows\Core\Model;

use stdClass;
use Zouriel\Workflows\Core\Json\Read;
use Zouriel\Workflows\Core\Json\Write;

/** A workflow variable, read as `vars.<key>` and written by `setVariables` (by id). Port of .NET `Variable`. */
final class Variable
{
    public string $id = '';

    public string $key = '';

    public string $label = '';

    /** A registry field type (default `text`). */
    public string $type = 'text';

    /** The value at the start of each run. */
    public mixed $default = null;

    public static function fromJson(mixed $value, string $path): self
    {
        $value = Read::itemObject($value, $path, 'Zouriel.Workflows.Model.Variable');
        $variable = new self;
        $variable->id = Read::string($value, 'id', $path);
        $variable->key = Read::string($value, 'key', $path);
        $variable->label = Read::string($value, 'label', $path);
        $variable->type = Read::string($value, 'type', $path, 'text');
        $variable->default = Read::value($value, 'default');

        return $variable;
    }

    public function toJson(): stdClass
    {
        return Write::object(['id' => $this->id, 'key' => $this->key, 'label' => $this->label, 'type' => $this->type, 'default' => $this->default]);
    }
}
