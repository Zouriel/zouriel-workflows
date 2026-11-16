<?php

namespace Zouriel\Workflows\Core\Engine\Views;

/**
 * What a permissions change did: the summary plus whether anything changed, the nodes the draft no longer
 * has, and (lifecycles) how many record instances moved to the new version or stayed. Port of .NET `PermissionsSummary`.
 */
final readonly class PermissionsSummary
{
    /** @param  list<string>  $draftSkipped */
    public function __construct(public DefinitionSummary $summary, public bool $changed, public array $draftSkipped, public int $recordsMoved, public int $recordsKept = 0) {}
}
