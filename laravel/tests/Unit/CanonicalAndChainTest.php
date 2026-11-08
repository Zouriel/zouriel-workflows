<?php

use Zouriel\Workflows\Core\Audit\AuditChain;
use Zouriel\Workflows\Core\Audit\AuditChainVerifier;
use Zouriel\Workflows\Core\Audit\AuditRecord;
use Zouriel\Workflows\Core\Json\Canonical;

// Cases spec/fixtures/audit/chain.json does not cover yet (docs/plans/go-and-laravel.md Phase 0,
// item 4). Expected text was computed independently in Python (keys sorted by UTF-16 code units).

it('orders keys by UTF-16 code units and escapes control characters in lower-case hex', function () {
    $value = (object) [
        "\u{FF5E}" => 1,
        "\u{1F600}" => 2,
        'a' => "x\u{1F}y\u{7}z",
        'b' => [1.5, -0.0, 1e21, 0.000001],
        "\u{E9}" => null,
    ];

    expect(Canonical::encode($value))->toBe('{"a":"x\u001fy\u0007z","b":[1.5,0,1e+21,0.000001],'."\"\u{E9}\":null,\"\u{1F600}\":2,\"\u{FF5E}\":1}");
});

it('refuses NaN and Infinity', function (float $n) {
    Canonical::encode([$n]);
})->with([NAN, INF])->throws(InvalidArgumentException::class);

function chainedRecord(int $seq, string $prevHash): AuditRecord
{
    $record = new AuditRecord(id: "e{$seq}", at: '2026-10-19T09:00:00.000Z', actorType: 'system', category: 'step',
        action: 'completed', subjectType: 'step', subjectId: "s{$seq}", correlationId: 'i1', seq: $seq, prevHash: $prevHash);
    $record->hash = AuditChain::computeHash($record);

    return $record;
}

it('finds an entry that is intact but links to the wrong predecessor', function () {
    $first = chainedRecord(1, AuditChain::GENESIS_HASH);
    $second = chainedRecord(2, str_repeat('a', 64));

    $result = AuditChainVerifier::verify([$first, $second]);

    expect($result->valid)->toBeFalse()
        ->and($result->brokenAtSeq)->toBe(2)
        ->and($result->problem)->toBe('Entry does not link to the entry before it.');
});

it('finds a first entry that does not start the chain', function () {
    expect(AuditChainVerifier::verify([chainedRecord(1, str_repeat('b', 64))])->problem)->toBe('The first entry does not start the chain.');
});

it('finds a repeated sequence number', function () {
    $first = chainedRecord(1, AuditChain::GENESIS_HASH);

    expect(AuditChainVerifier::verify([$first, chainedRecord(1, $first->hash)])->problem)->toBe('Sequence number 1 is repeated or out of order.');
});
