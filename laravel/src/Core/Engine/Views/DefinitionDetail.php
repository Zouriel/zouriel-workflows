<?php

namespace Zouriel\Workflows\Core\Engine\Views;

use stdClass;
use Zouriel\Workflows\Core\Lint\LintProblem;

/** A definition with its draft, latest published version, history and the draft's lint problems. */
final readonly class DefinitionDetail
{
    /**
     * @param  list<DefinitionVersionInfo>  $versions  newest first
     * @param  list<LintProblem>  $problems
     */
    public function __construct(public DefinitionSummary $summary, public stdClass $draft, public ?stdClass $published, public array $versions, public array $problems) {}
}
