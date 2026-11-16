<?php

namespace Zouriel\Workflows\Core\Host;

/** Where users, roles, groups, departments, portals, queues and managers come from (spec §8). Port of .NET `IWorkflowIdentity`. */
interface WorkflowIdentity
{
    /** A person by id, or null when unknown. */
    public function user(string $id): ?WorkflowUser;

    /** @return list<string> the ids of everyone with a role */
    public function usersInRole(string $role): array;

    /** @return list<string> */
    public function usersInGroup(string $group): array;

    /** @return list<string> */
    public function usersInDepartment(string $department): array;

    /** @return list<string> */
    public function usersInPortal(string $portal): array;

    /** @return list<string> */
    public function usersInQueue(string $queue): array;

    /** A person's manager's id, or null (spec §8 `managerOf`). */
    public function manager(string $userId): ?string;

    /** @return list<WorkflowUser> people whose name or id contains `$query` (for pickers), at most `$limit` */
    public function searchUsers(?string $query, int $limit): array;
}
