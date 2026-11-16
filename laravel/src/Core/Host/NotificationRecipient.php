<?php

namespace Zouriel\Workflows\Core\Host;

use stdClass;

/** One recipient of a notification: a user (with their email, if known) or a bare email address. Port of .NET `NotificationRecipient`. */
final readonly class NotificationRecipient
{
    public function __construct(public ?string $userId, public ?string $email, public ?string $name) {}

    public function toJson(): stdClass
    {
        return (object) ['userId' => $this->userId, 'email' => $this->email, 'name' => $this->name];
    }

    public static function fromJson(stdClass $value): self
    {
        return new self($value->userId ?? null, $value->email ?? null, $value->name ?? null);
    }
}
