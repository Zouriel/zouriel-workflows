<?php

namespace Zouriel\Workflows\Core\Audit;

/**
 * Checks an audit chain entry by entry, oldest first, so it can run over pages: each entry's own
 * hash, each link to the entry before it, strictly increasing sequence numbers, and that every
 * gap is exactly covered by the ranges purges recorded. A port of .NET's `AuditChainVerifier`,
 * with the same problem wording.
 */
final class AuditChainVerifier
{
    /** @var list<array{0: int, 1: int}> */
    private array $purged;

    /** @var list<array{0: int, 1: int}> */
    private array $gaps = [];

    private ?AuditRecord $previous = null;

    public int $checked = 0;

    public ?AuditVerification $failure = null;

    /** @param  list<array{0: int, 1: int}>  $purged  [from, to] ranges recorded by purges */
    public function __construct(array $purged = [], private readonly int $firstSeq = 1)
    {
        usort($purged, fn ($a, $b) => $a[0] <=> $b[0]);
        $this->purged = $purged;
    }

    /** @param  iterable<AuditRecord>  $entries */
    public static function verify(iterable $entries, array $purged = []): AuditVerification
    {
        $verifier = new self($purged);
        foreach ($entries as $entry) {
            if (! $verifier->add($entry)) {
                break;
            }
        }

        return $verifier->result();
    }

    /** Checks the next entry; false once the chain is broken. */
    public function add(AuditRecord $e): bool
    {
        if ($this->failure !== null) {
            return false;
        }
        $this->checked++;
        if (! hash_equals(AuditChain::computeHash($e), $e->hash)) {
            return $this->fail($e->seq, 'Entry content does not match its hash.');
        }
        if ($this->previous === null) {
            if ($e->seq === 1 && $e->prevHash !== AuditChain::GENESIS_HASH) {
                return $this->fail($e->seq, 'The first entry does not start the chain.');
            }
            if ($e->seq > $this->firstSeq && ! $this->covered($this->firstSeq, $e->seq - 1)) {
                return $this->fail($this->firstSeq, "Entries {$this->firstSeq}–".($e->seq - 1).' are missing and no purge covers them.');
            }
        } elseif ($e->seq <= $this->previous->seq) {
            return $this->fail($e->seq, "Sequence number {$e->seq} is repeated or out of order.");
        } elseif ($e->seq === $this->previous->seq + 1) {
            if ($e->prevHash !== $this->previous->hash) {
                return $this->fail($e->seq, 'Entry does not link to the entry before it.');
            }
        } else {
            $from = $this->previous->seq + 1;
            $to = $e->seq - 1;
            if (! $this->covered($from, $to)) {
                return $this->fail($from, "Entries {$from}–{$to} are missing and no purge covers them.");
            }
            $this->gaps[] = [$from, $to];
        }
        $this->previous = $e;

        return true;
    }

    /** The verdict; with the chain's head, entries missing at the end must be purged ones too. */
    public function result(?array $head = null): AuditVerification
    {
        if ($this->failure !== null || $head === null) {
            return $this->failure ?? new AuditVerification(true, $this->checked, null, null, $this->gaps);
        }
        [$headSeq, $headHash] = $head;
        $last = $this->previous?->seq ?? $this->firstSeq - 1;
        if ($headSeq < $last) {
            return new AuditVerification(false, $this->checked, $headSeq, 'The chain head is behind its newest entry.', $this->gaps);
        }
        if ($headSeq === $last && $this->previous !== null && $headHash !== $this->previous->hash) {
            return new AuditVerification(false, $this->checked, $last, 'The newest entry does not match the chain head.', $this->gaps);
        }
        if ($headSeq > $last && ! $this->covered($last + 1, $headSeq)) {
            return new AuditVerification(false, $this->checked, $last + 1, 'Entries '.($last + 1)."–{$headSeq} are missing and no purge covers them.", $this->gaps);
        }

        return new AuditVerification(true, $this->checked, null, null, $this->gaps);
    }

    private function fail(int $seq, string $problem): bool
    {
        $this->failure = new AuditVerification(false, $this->checked, $seq, $problem, $this->gaps);

        return false;
    }

    /** Whether every sequence number in [from, to] lies in a recorded purge range. */
    private function covered(int $from, int $to): bool
    {
        $next = $from;
        foreach ($this->purged as [$rangeFrom, $rangeTo]) {
            if ($rangeTo < $next) {
                continue;
            }
            if ($rangeFrom > $next) {
                return false;
            }
            $next = $rangeTo + 1;
            if ($next > $to) {
                return true;
            }
        }

        return $next > $to;
    }
}
