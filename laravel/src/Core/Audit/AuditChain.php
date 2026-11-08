<?php

namespace Zouriel\Workflows\Core\Audit;

use stdClass;
use Zouriel\Forms\Json;
use Zouriel\Workflows\Core\Json\Canonical;

/**
 * The audit hash chain (spec §11): `sha256_hex(prevHash + "\n" + jcs(entry))`, where the entry
 * is every field except prevHash and hash, nulls left out. A port of .NET's `AuditChain`; an
 * export from any engine verifies with any other engine's verifier.
 */
final class AuditChain
{
    public const string GENESIS_HASH = '0000000000000000000000000000000000000000000000000000000000000000';

    /** The object that is hashed. */
    public static function hashedObject(AuditRecord $r): stdClass
    {
        $actor = (object) ['type' => $r->actorType];
        if ($r->actorId !== null) {
            $actor->id = $r->actorId;
        }
        if ($r->actorName !== null) {
            $actor->name = $r->actorName;
        }
        $subject = (object) ['type' => $r->subjectType, 'id' => $r->subjectId];
        if ($r->entity !== null) {
            $subject->entity = $r->entity;
        }
        if ($r->entityId !== null) {
            $subject->entityId = $r->entityId;
        }
        $o = (object) [
            'seq' => (float) $r->seq,
            'id' => $r->id,
            'at' => $r->at,
            'actor' => $actor,
            'category' => $r->category,
            'action' => $r->action,
            'subject' => $subject,
            'correlationId' => $r->correlationId,
        ];
        foreach (['tenant', 'instanceId', 'definitionId'] as $key) {
            if ($r->{$key} !== null) {
                $o->{$key} = $r->{$key};
            }
        }
        foreach (['before', 'after', 'data'] as $key) {
            if ($r->{$key} !== null) {
                $o->{$key} = Json::decode($r->{$key});
            }
        }
        if ($r->causationId !== null) {
            $o->causationId = $r->causationId;
        }

        return $o;
    }

    public static function computeHash(AuditRecord $r): string
    {
        return hash('sha256', $r->prevHash."\n".Canonical::encode(self::hashedObject($r)));
    }
}
