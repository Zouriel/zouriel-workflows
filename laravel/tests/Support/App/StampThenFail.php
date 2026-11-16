<?php

namespace Zouriel\Workflows\Tests\Support\App;

use RuntimeException;
use stdClass;
use Zouriel\Workflows\Core\Host\ActionContext;
use Zouriel\Workflows\Core\Host\WorkflowAction;

/** An action that saves record writes and then fails: everything it wrote must be undone. */
final class StampThenFail implements WorkflowAction
{
    public function execute(ActionContext $context): stdClass
    {
        $application = Application::query()->findOrFail($context->entityId);
        $application->notes = 'half done';
        $application->save();
        Programme::query()->create(['name' => ['en' => 'created by a failed action', 'dv' => '-']]);

        throw new RuntimeException('The finance system is down.');
    }
}
