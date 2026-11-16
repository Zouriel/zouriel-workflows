<?php

namespace Zouriel\Workflows\Core\Engine;

/** The people an approval's `makerChecker` rule refuses (spec §7). Port of .NET `WorkflowEngine.Tasks.cs` `Sod`. */
final class Sod
{
    /** @var array<string, string> user id → why */
    public array $excluded = [];

    public ?string $requiredRole = null;

    public ?string $roleLabel = null;

    /** @var array<string, true> */
    public array $roleHolders = [];

    /** Why a person is refused (without a full stop), or null. @param  list<string>|null  $actorRoles */
    public function refused(string $userId, ?array $actorRoles = null): ?string
    {
        if (isset($this->excluded[$userId])) {
            return $this->excluded[$userId];
        }
        if ($this->requiredRole !== null && ! isset($this->roleHolders[$userId]) && ! in_array($this->requiredRole, $actorRoles ?? [], true)) {
            return "it needs the {$this->roleLabel} role";
        }

        return null;
    }

    public function exclude(string $userId, string $why): void
    {
        $this->excluded[$userId] ??= $why;
    }
}
