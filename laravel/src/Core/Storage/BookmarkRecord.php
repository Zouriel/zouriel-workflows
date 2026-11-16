<?php

namespace Zouriel\Workflows\Core\Storage;

/**
 * A durable pause point: a timer, a person, a signal or a condition. `lockedUntil` is set while a
 * worker fires it, holds the backoff after a failure, and `Instant::MAX` means parked for an admin.
 * Port of .NET `BookmarkRecord`.
 */
final class BookmarkRecord
{
    public string $id = '';

    public ?string $instanceId = null;

    public ?string $tokenId = null;

    public ?string $nodeId = null;

    public string $kind = BookmarkKinds::TIMER;

    public string $purpose = BookmarkPurposes::WAIT;

    public ?int $dueAt = null;

    public ?string $signal = null;

    public ?string $entity = null;

    public ?string $entityId = null;

    public ?string $definitionId = null;

    public ?string $taskId = null;

    public ?string $payload = null;

    public int $createdAt = 0;

    public ?int $lockedUntil = null;

    public int $attempts = 0;

    public ?string $error = null;
}
