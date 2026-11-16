<?php

namespace Zouriel\Workflows\Core\Model;

use stdClass;
use Zouriel\Workflows\Core\Json\Read;
use Zouriel\Workflows\Core\Json\Write;

/**
 * One entry of an assignee list (spec §8): a role, group, department, portal, queue, user, user field,
 * manager, the initiator, or (recipients only) an email address template. Port of .NET `Assignee`.
 */
final class Assignee
{
    public ?string $role = null;

    public ?string $group = null;

    public ?string $department = null;

    public ?string $portal = null;

    public ?string $queue = null;

    /** One user by id. */
    public ?string $user = null;

    /** The user in a user field (a token path such as `entity.reviewer`). */
    public ?string $field = null;

    /** The manager of `initiator`, of the current `assignees`, or of the user at a token path. */
    public ?string $managerOf = null;

    public ?bool $initiator = null;

    /** Only for notification recipients: an address template. */
    public ?Template $email = null;

    public static function fromJson(mixed $value, string $path): self
    {
        $value = Read::itemObject($value, $path, 'Zouriel.Workflows.Model.Assignee');
        $assignee = new self;
        foreach (['role', 'group', 'department', 'portal', 'queue', 'user', 'field', 'managerOf'] as $key) {
            $assignee->{$key} = Read::optionalString($value, $key, $path);
        }
        $assignee->initiator = Read::optionalBool($value, 'initiator', $path);
        $assignee->email = Template::fromJson(Read::value($value, 'email'), "{$path}.email");

        return $assignee;
    }

    /** @return list<self>|null */
    public static function list(stdClass $object, string $key, string $path): ?array
    {
        return Read::list($object, $key, $path, self::fromJson(...));
    }

    public function toJson(): stdClass
    {
        return Write::object([
            'role' => $this->role, 'group' => $this->group, 'department' => $this->department, 'portal' => $this->portal,
            'queue' => $this->queue, 'user' => $this->user, 'field' => $this->field, 'managerOf' => $this->managerOf,
            'initiator' => $this->initiator, 'email' => $this->email?->toJson(),
        ]);
    }
}
