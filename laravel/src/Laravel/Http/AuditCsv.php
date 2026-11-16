<?php

namespace Zouriel\Workflows\Laravel\Http;

use Zouriel\Workflows\Core\Audit\AuditRecord;

/** The audit log as CSV, column for column as .NET's export (`GET /audit/export?format=csv`). */
final class AuditCsv
{
    public const string HEADER = "seq,at,actorType,actorId,actorName,category,action,subjectType,subjectId,entity,entityId,instanceId,definitionId,before,after,data,correlationId,prevHash,hash\n";

    public static function row(AuditRecord $r): string
    {
        return implode(',', [(string) $r->seq, ...array_map(self::cell(...), [$r->at, $r->actorType, $r->actorId, $r->actorName, $r->category, $r->action,
            $r->subjectType, $r->subjectId, $r->entity, $r->entityId, $r->instanceId, $r->definitionId, $r->before, $r->after, $r->data, $r->correlationId,
            $r->prevHash, $r->hash])])."\n";
    }

    /**
     * One quoted CSV cell. Text a spreadsheet would run as a formula (starting with = + - @, tab or carriage return) gets
     * a leading apostrophe, so a record title cannot execute when an admin opens the export.
     */
    public static function cell(?string $s): string
    {
        if ($s === null) {
            return '';
        }
        if ($s !== '' && in_array($s[0], ['=', '+', '-', '@', "\t", "\r"], true)) {
            $s = "'".$s;
        }

        return '"'.str_replace('"', '""', $s).'"';
    }
}
