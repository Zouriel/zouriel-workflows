<?php

namespace Zouriel\Workflows\Core\Settings;

use stdClass;
use Zouriel\Workflows\Core\Json\Read;

/** An entity whose triggers are paused. Port of .NET `PausedEntity`. */
final class PausedEntity
{
    public string $entity = '';

    public static function fromJson(mixed $value, string $path): self
    {
        $value = Read::itemObject($value, $path, 'Zouriel.Workflows.Settings.PausedEntity');
        $paused = new self;
        $paused->entity = Read::string($value, 'entity', $path);

        return $paused;
    }

    public function toJson(): stdClass
    {
        return (object) ['entity' => $this->entity];
    }
}
