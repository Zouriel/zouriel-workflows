<?php

namespace Zouriel\Workflows\Core\Engine\Views;

use stdClass;
use Zouriel\Workflows\Core\Lint\LintProblem;

/** What a test run did, masked like the audit log. Port of .NET `SimulationResult`. */
final readonly class SimulationResult
{
    /**
     * @param  list<LintProblem>  $problems
     * @param  list<SimulationEntry>  $log
     * @param  list<InstanceView>  $instances
     * @param  list<TaskView>  $openTasks
     */
    public function __construct(public array $problems, public array $log, public array $instances, public array $openTasks, public ?stdClass $record, public ?string $error) {}
}
