<?php

use Zouriel\Forms\Json;
use Zouriel\Workflows\Core\Audit\AuditChain;
use Zouriel\Workflows\Core\Audit\AuditChainVerifier;
use Zouriel\Workflows\Core\Audit\AuditRecord;
use Zouriel\Workflows\Core\Json\Canonical;

if (! specAvailable()) {
    test('conformance runs in the zouriel-workflows monorepo, next to spec/')->skip('spec/fixtures not found');

    return;
}

/** An AuditRecord from a fixture entry, its JSON fields stored as text the way a store keeps them. */
function auditRecord(stdClass $item): AuditRecord
{
    $e = $item->entry;
    $text = fn (string $key) => property_exists($e, $key) ? Json::encode($e->{$key}) : null;

    return new AuditRecord(
        id: $e->id, at: $e->at, actorType: $e->actor->type, category: $e->category, action: $e->action,
        subjectType: $e->subject->type, subjectId: $e->subject->id, correlationId: $e->correlationId,
        actorId: $e->actor->id ?? null, actorName: $e->actor->name ?? null, tenant: $e->tenant ?? null,
        entity: $e->subject->entity ?? null, entityId: $e->subject->entityId ?? null,
        instanceId: $e->instanceId ?? null, definitionId: $e->definitionId ?? null,
        before: $text('before'), after: $text('after'), data: $text('data'), causationId: $e->causationId ?? null,
        seq: (int) $e->seq, prevHash: $item->prevHash, hash: $item->hash,
    );
}

function auditChainCases(): array
{
    $out = [];
    foreach (specFixture('audit/chain.json')->entries as $item) {
        $out['entry '.$item->entry->seq] = [$item];
    }

    return $out;
}

it('writes each entry in canonical form, byte for byte', function (stdClass $item) {
    expect(Canonical::encode(AuditChain::hashedObject(auditRecord($item))))->toBe($item->canonical);
})->with(auditChainCases());

it('hashes each entry onto the one before it', function (stdClass $item) {
    expect(AuditChain::computeHash(auditRecord($item)))->toBe($item->hash);
})->with(auditChainCases());

it('verifies the whole chain', function () {
    $records = array_map(auditRecord(...), specFixture('audit/chain.json')->entries);
    $result = AuditChainVerifier::verify($records);

    expect($result->valid)->toBeTrue((string) $result->problem)
        ->and($result->checked)->toBe(count($records));
});

describe('a tampered chain', function () {
    it('is caught when an entry’s content changes', function () {
        $records = array_map(auditRecord(...), specFixture('audit/chain.json')->entries);
        $records[1]->data = '{"outputs":{"sent":4}}';

        $result = AuditChainVerifier::verify($records);

        expect($result->valid)->toBeFalse()
            ->and($result->brokenAtSeq)->toBe(2)
            ->and($result->problem)->toBe('Entry content does not match its hash.');
    });

    it('is caught when an entry is removed without a purge', function () {
        $records = array_map(auditRecord(...), specFixture('audit/chain.json')->entries);
        unset($records[1]);

        $result = AuditChainVerifier::verify(array_values($records));

        expect($result->valid)->toBeFalse()->and($result->problem)->toBe('Entries 2–2 are missing and no purge covers them.');
    });

    it('accepts a gap that a recorded purge explains', function () {
        $records = array_map(auditRecord(...), specFixture('audit/chain.json')->entries);
        unset($records[1]);

        $result = AuditChainVerifier::verify(array_values($records), purged: [[2, 2]]);

        expect($result->valid)->toBeTrue((string) $result->problem)->and($result->purgedGaps)->toBe([[2, 2]]);
    });

    it('is caught when the head is ahead of the newest entry', function () {
        $records = array_map(auditRecord(...), specFixture('audit/chain.json')->entries);
        $verifier = new AuditChainVerifier;
        foreach ($records as $record) {
            $verifier->add($record);
        }
        $last = end($records);

        expect($verifier->result([$last->seq, $last->hash])->valid)->toBeTrue()
            ->and($verifier->result([$last->seq + 1, 'x'])->problem)->toBe('Entries '.($last->seq + 1).'–'.($last->seq + 1).' are missing and no purge covers them.');
    });
});
