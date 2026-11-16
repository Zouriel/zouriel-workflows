<?php

namespace Zouriel\Workflows\Core\Lint;

use Zouriel\Workflows\Core\Model\WorkflowDefinition;
use Zouriel\Workflows\Core\Registry\WorkflowRegistry;
use Zouriel\Workflows\Core\Settings\GlobalSettings;

/**
 * What a definition is checked against. Port of .NET `LintContext`.
 *
 * @param  list<WorkflowDefinition>  $otherPublished  the other published definitions (lifecycle conflicts, sub-workflows, owned fields)
 */
final readonly class LintContext
{
    public function __construct(public WorkflowRegistry $registry, public GlobalSettings $settings, public array $otherPublished = []) {}
}
