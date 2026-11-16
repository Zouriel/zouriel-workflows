<?php

namespace Zouriel\Workflows\Core\Storage;

/** A task for people: an approval or a task step with outcomes. JSON members are text. Port of .NET `TaskRecord`. */
final class TaskRecord
{
    public string $id = '';

    public string $instanceId = '';

    public ?string $rootInstanceId = null;

    public string $nodeId = '';

    public string $tokenId = '';

    public string $kind = 'approval';

    public string $title = '';

    public ?string $instructions = null;

    public string $definitionKey = '';

    public ?string $entity = null;

    public ?string $entityId = null;

    public ?string $entityTitle = null;

    public string $status = TaskStatus::OPEN;

    /** JSON array of user ids who may decide. */
    public string $assignees = '[]';

    /** JSON `{type, value}` for approvals. */
    public ?string $rule = null;

    /** JSON array of `{key, label, tone}`. */
    public string $outcomes = '[]';

    /** JSON array of `{userId, name, outcome, comment, data, at}`. */
    public string $decisions = '[]';

    public ?string $form = null;

    public ?string $requireComment = null;

    public bool $allowDelegation = false;

    public ?string $outcome = null;

    public int $dueAt = 0;

    public int $escalationLevel = 0;

    public int $createdAt = 0;

    public ?int $completedAt = null;

    public int $rowVersion = 0;
}
