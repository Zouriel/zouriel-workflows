<?php

namespace Zouriel\Workflows\Core\Host;

/** The default directory: users exist by id, nobody has a role (.NET `WorkflowEngineOptions.cs` `NoIdentity`). */
final class NoIdentity implements WorkflowIdentity
{
    public function user(string $id): ?WorkflowUser
    {
        return new WorkflowUser($id, $id);
    }

    public function usersInRole(string $role): array
    {
        return [];
    }

    public function usersInGroup(string $group): array
    {
        return [];
    }

    public function usersInDepartment(string $department): array
    {
        return [];
    }

    public function usersInPortal(string $portal): array
    {
        return [];
    }

    public function usersInQueue(string $queue): array
    {
        return [];
    }

    public function manager(string $userId): ?string
    {
        return null;
    }

    public function searchUsers(?string $query, int $limit): array
    {
        return [];
    }
}
