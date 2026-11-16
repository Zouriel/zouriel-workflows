<?php

namespace Zouriel\Workflows\Core\Engine\Views;

use stdClass;

/** One person's decision on a task. Port of .NET `TaskDecision`. */
final readonly class TaskDecision
{
    public function __construct(public string $userId, public ?string $name, public string $outcome, public ?string $comment, public ?stdClass $data, public string $at) {}

    public function toValue(): stdClass
    {
        return (object) ['userId' => $this->userId, 'name' => $this->name, 'outcome' => $this->outcome, 'comment' => $this->comment, 'data' => $this->data, 'at' => $this->at];
    }
}
