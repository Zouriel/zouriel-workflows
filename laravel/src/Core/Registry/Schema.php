<?php

namespace Zouriel\Workflows\Core\Registry;

use stdClass;
use Zouriel\Workflows\Core\Lint\Linter;

/**
 * The body of `GET /schema` (spec/api/openapi.json `SchemaResponse`): the spec version, the registry as
 * editors see it and the step types with their labels and outputs. Port of .NET `WorkflowApi`'s
 * `/schema` endpoint, which writes with ASP.NET's web JSON defaults (null members included).
 */
final class Schema
{
    public static function response(WorkflowRegistry $registry): stdClass
    {
        return (object) [
            'specVersion' => 1,
            'registry' => $registry->forEditors()->toApiJson(),
            'steps' => array_map(
                fn (string $type, string $label) => (object) ['type' => $type, 'label' => $label, 'outputs' => Linter::STEP_OUTPUTS[$type] ?? []],
                array_keys(Linter::STEP_LABELS),
                Linter::STEP_LABELS,
            ),
        ];
    }
}
