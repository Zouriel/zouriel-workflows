<?php

namespace Zouriel\Workflows\Core\Storage;

/** What a purge deleted: how many entries, and exactly which sequence numbers (inclusive [from, to] runs). */
final readonly class AuditPurgeResult
{
    /** @param  list<array{0: int, 1: int}>  $ranges */
    public function __construct(public int $count, public array $ranges) {}

    /**
     * Ascending sequence numbers as the fewest inclusive runs (.NET `SeqRange.Compress`).
     *
     * @param  list<int>  $ascending
     * @return list<array{0: int, 1: int}>
     */
    public static function compress(array $ascending): array
    {
        $list = [];
        foreach ($ascending as $seq) {
            $last = count($list) - 1;
            if ($last >= 0 && $list[$last][1] + 1 === $seq) {
                $list[$last][1] = $seq;
            } elseif ($last < 0 || $seq > $list[$last][1]) {
                $list[] = [$seq, $seq];
            }
        }

        return $list;
    }
}
