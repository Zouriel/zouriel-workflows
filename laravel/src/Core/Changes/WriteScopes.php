<?php

namespace Zouriel\Workflows\Core\Changes;

use Zouriel\Workflows\Core\Host\WorkflowActor;

/**
 * The stack of write scopes of the current request (.NET keeps it in an `AsyncLocal`). The engine owns
 * one; the Laravel layer binds it per request, so scopes never leak between requests.
 */
final class WriteScopes
{
    /** @var list<WriteScope> */
    private array $stack = [];

    /** The innermost scope, or null outside workflow code. */
    public function current(): ?WriteScope
    {
        return $this->stack[count($this->stack) - 1] ?? null;
    }

    /** A new scope; it suppresses triggers when asked or when the current one does (nested scopes inherit it). */
    public function open(WorkflowActor $actor, int $depth, ?string $instanceId = null, ?string $definitionId = null, ?string $correlationId = null,
        bool $suppressTriggers = false): WriteScope
    {
        return new WriteScope($actor, $depth, $instanceId, $definitionId, $correlationId, $suppressTriggers || $this->current()?->suppressTriggers === true);
    }

    /**
     * Runs `$work` inside `$scope`.
     *
     * @template T
     *
     * @param  callable(): T  $work
     * @return T
     */
    public function within(WriteScope $scope, callable $work): mixed
    {
        $this->stack[] = $scope;
        try {
            return $work();
        } finally {
            array_pop($this->stack);
        }
    }

    /**
     * Opens a scope and runs `$work` inside it.
     *
     * @template T
     *
     * @param  callable(): T  $work
     * @return T
     */
    public function run(WorkflowActor $actor, int $depth, ?string $instanceId, ?string $definitionId, ?string $correlationId, callable $work,
        bool $suppressTriggers = false): mixed
    {
        return $this->within($this->open($actor, $depth, $instanceId, $definitionId, $correlationId, $suppressTriggers), $work);
    }
}
