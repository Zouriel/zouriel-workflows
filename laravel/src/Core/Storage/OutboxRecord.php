<?php

namespace Zouriel\Workflows\Core\Storage;

/**
 * A row of the transactional outbox: work done after the unit that wrote it commits. Rows of one record
 * (`recordKey` = `entity:id`) are processed in the order they were written (spec §5). Port of .NET `OutboxRecord`.
 */
final class OutboxRecord
{
    public string $id = '';

    public string $kind = OutboxKinds::CHANGE;

    public string $payload = '{}';

    public int $createdAt = 0;

    public ?int $processedAt = null;

    public int $attempts = 0;

    public ?int $lockedUntil = null;

    public ?string $error = null;

    public ?string $recordKey = null;

    private static int $counter = 0;

    public static function keyOf(string $entity, string $id): string
    {
        return "{$entity}:{$id}";
    }

    /** A 32-character id that sorts after every id this process made before: the clock in milliseconds, a counter, then random hex. */
    public static function newId(int $now): string
    {
        self::$counter++;

        return substr(sprintf('%013d%07d', max(0, $now), self::$counter % 10000000).bin2hex(random_bytes(16)), 0, 32);
    }
}
