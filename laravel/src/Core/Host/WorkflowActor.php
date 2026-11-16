<?php

namespace Zouriel\Workflows\Core\Host;

/**
 * Who did something: `user`, `system` or `workflow` (anything else, e.g. `anonymous`, is not a person).
 * `id` is the user id (the instance id for workflows), `roles` the roles a person carries from their
 * sign-in (they count for `who` and manual starts). Port of .NET `Abstractions.cs` `WorkflowActor`.
 */
final readonly class WorkflowActor
{
    /** @param  list<string>|null  $roles */
    public function __construct(public string $type, public ?string $id = null, public ?string $name = null, public ?array $roles = null) {}

    /** The engine itself (timers, automatic transitions, app code). */
    public static function system(): self
    {
        return new self('system', null, 'System');
    }

    /** @param  list<string>|null  $roles */
    public static function user(string $id, ?string $name = null, ?array $roles = null): self
    {
        return new self('user', $id, $name, $roles);
    }

    /** A workflow instance acting on its own (its writes and steps). */
    public static function workflow(string $definitionKey, string $instanceId): self
    {
        return new self('workflow', $instanceId, $definitionKey);
    }

    /** True for a person with an id; only persons may apply transitions, decide tasks or start manual workflows. */
    public function isUser(): bool
    {
        return $this->type === 'user' && $this->id !== null;
    }
}
