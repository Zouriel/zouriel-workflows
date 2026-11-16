<?php

namespace Zouriel\Workflows\Core\Host;

/** A person the app's directory knows (spec §8). Port of .NET `Abstractions.cs` `WorkflowUser`. */
final readonly class WorkflowUser
{
    /**
     * @param  list<string>  $roles
     * @param  list<string>  $groups
     * @param  list<string>  $departments
     * @param  list<string>  $portals
     * @param  list<string>  $queues
     */
    public function __construct(
        public string $id,
        public string $name,
        public ?string $email = null,
        public array $roles = [],
        public array $groups = [],
        public array $departments = [],
        public array $portals = [],
        public array $queues = [],
    ) {}
}
