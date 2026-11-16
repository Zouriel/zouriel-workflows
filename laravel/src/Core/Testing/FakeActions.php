<?php

namespace Zouriel\Workflows\Core\Testing;

use stdClass;
use Zouriel\Forms\Json;
use Zouriel\Workflows\Core\Host\ActionContext;
use Zouriel\Workflows\Core\Host\ActionResolver;
use Zouriel\Workflows\Core\Host\ActionUndoContext;
use Zouriel\Workflows\Core\Host\UndoableWorkflowAction;
use Zouriel\Workflows\Core\Host\WorkflowAction;
use Zouriel\Workflows\Core\InvalidOperationException;
use Zouriel\Workflows\Core\Registry\ActionDescriptor;

/** Action handlers for tests: each key returns fixed outputs, optionally failing the first N calls; undoable when asked. */
final class FakeActions implements ActionResolver
{
    /** @var list<ActionContext> every call so far, in order */
    public array $calls = [];

    /** @var list<ActionUndoContext> every undo a reset asked for */
    public array $undone = [];

    /** @var array<string, array{output: stdClass, failTimes: int, failMessage: string, calls: int}> */
    private array $behaviours = [];

    /** @var array<string, true> */
    private array $undoable = [];

    public function set(string $key, ?stdClass $output = null, int $failTimes = 0, ?string $failMessage = null): void
    {
        $this->behaviours[$key] = ['output' => $output ?? new stdClass, 'failTimes' => $failTimes, 'failMessage' => $failMessage ?? 'The action failed.', 'calls' => 0];
    }

    /** Lets a reset undo an action. */
    public function makeUndoable(string $key): void
    {
        $this->undoable[$key] = true;
    }

    public function resolve(ActionDescriptor $action): WorkflowAction
    {
        $run = function (ActionContext $context) use ($action): stdClass {
            $this->calls[] = $context;
            $this->behaviours[$action->key] ??= ['output' => new stdClass, 'failTimes' => 0, 'failMessage' => '', 'calls' => 0];
            $b = &$this->behaviours[$action->key];
            $b['calls']++;
            if ($b['calls'] <= $b['failTimes']) {
                throw new InvalidOperationException($b['failMessage']);
            }

            return Json::copy($b['output']);
        };
        $undo = fn (ActionUndoContext $context) => $this->undone[] = $context;

        return isset($this->undoable[$action->key])
            ? new readonly class($run, $undo) implements UndoableWorkflowAction
            {
                public function __construct(private \Closure $run, private \Closure $undo) {}

                public function execute(ActionContext $context): stdClass
                {
                    return ($this->run)($context);
                }

                public function undo(ActionUndoContext $context): void
                {
                    ($this->undo)($context);
                }
            }
        : new readonly class($run) implements WorkflowAction
        {
            public function __construct(private \Closure $run) {}

            public function execute(ActionContext $context): stdClass
            {
                return ($this->run)($context);
            }
        };
    }
}
