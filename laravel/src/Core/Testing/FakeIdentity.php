<?php

namespace Zouriel\Workflows\Core\Testing;

use Zouriel\Workflows\Core\Host\WorkflowIdentity;
use Zouriel\Workflows\Core\Host\WorkflowUser;
use Zouriel\Workflows\Core\InvalidOperationException;

/** People for tests: users with roles, groups, departments, portals, queues and a manager, added by hand. Port of .NET `Testing/WorkflowTestHost.cs` `FakeIdentity`. */
final class FakeIdentity implements WorkflowIdentity
{
    /** @var array<string, array{0: WorkflowUser, 1: ?string}> */
    private array $users = [];

    /** @var array<string, true> user ids whose lookup fails, to test a failing directory */
    public array $broken = [];

    public function add(WorkflowUser $user, ?string $manager = null): void
    {
        $this->users[$user->id] = [$user, $manager];
    }

    public function user(string $id): ?WorkflowUser
    {
        if (isset($this->broken[$id])) {
            throw new InvalidOperationException("The directory failed looking up '{$id}'.");
        }

        return $this->users[$id][0] ?? null;
    }

    /** @return list<string> */
    private function where(callable $has): array
    {
        $ids = array_map(fn ($u) => $u[0]->id, array_filter($this->users, fn ($u) => $has($u[0])));
        usort($ids, strcmp(...));

        return $ids;
    }

    public function usersInRole(string $role): array
    {
        return $this->where(fn (WorkflowUser $u) => in_array($role, $u->roles, true));
    }

    public function usersInGroup(string $group): array
    {
        return $this->where(fn (WorkflowUser $u) => in_array($group, $u->groups, true));
    }

    public function usersInDepartment(string $department): array
    {
        return $this->where(fn (WorkflowUser $u) => in_array($department, $u->departments, true));
    }

    public function usersInPortal(string $portal): array
    {
        return $this->where(fn (WorkflowUser $u) => in_array($portal, $u->portals, true));
    }

    public function usersInQueue(string $queue): array
    {
        return $this->where(fn (WorkflowUser $u) => in_array($queue, $u->queues, true));
    }

    public function manager(string $userId): ?string
    {
        return $this->users[$userId][1] ?? null;
    }

    public function searchUsers(?string $query, int $limit): array
    {
        $found = array_values(array_map(fn ($u) => $u[0], array_filter($this->users, fn ($u) => $query === null || $query === ''
            || stripos($u[0]->name, $query) !== false || stripos($u[0]->id, $query) !== false)));
        usort($found, fn (WorkflowUser $a, WorkflowUser $b) => strcmp($a->name, $b->name));

        return array_slice($found, 0, $limit);
    }
}
