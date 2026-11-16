<?php

namespace Zouriel\Workflows\Core\Model;

use JsonException;
use stdClass;
use Zouriel\Forms\Json;
use Zouriel\Workflows\Core\Json\JsonShapeException;
use Zouriel\Workflows\Core\Json\Read;

/**
 * Reading and writing definitions. Port of .NET `Model/Converters.cs` `Definitions`: `parse()` throws
 * a `JsonException` (invalid JSON text, or a document that is not a definition, with the messages of
 * the .NET converters), which the engine reports as `invalidJson`.
 */
final class Definitions
{
    /** @throws JsonException the text is not a definition */
    public static function parse(string|stdClass $json): WorkflowDefinition
    {
        $value = is_string($json) ? Json::decode($json) : $json;
        if ($value === null) {
            throw new JsonShapeException('Empty definition.');
        }

        return WorkflowDefinition::fromJson(Read::itemObject($value, '$', 'Zouriel.Workflows.Model.WorkflowDefinition'));
    }

    /** The definition as compact JSON: members in declaration order, nulls left out (as .NET writes it). */
    public static function serialize(WorkflowDefinition $definition): string
    {
        return Json::encode($definition->toJson());
    }
}
