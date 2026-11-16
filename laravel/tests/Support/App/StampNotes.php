<?php

namespace Zouriel\Workflows\Tests\Support\App;

use stdClass;
use Zouriel\Workflows\Core\Host\ActionContext;
use Zouriel\Workflows\Core\Host\WorkflowAction;

/** An action that writes to the record through its model and reports what it wrote. */
final class StampNotes implements WorkflowAction
{
    public function execute(ActionContext $context): stdClass
    {
        $application = Application::query()->findOrFail($context->entityId);
        $application->notes = $context->input->text;
        $application->save();

        return (object) ['length' => (float) mb_strlen($context->input->text)];
    }
}
