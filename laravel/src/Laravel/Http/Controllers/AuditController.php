<?php

namespace Zouriel\Workflows\Laravel\Http\Controllers;

use Illuminate\Http\Request;
use Illuminate\Http\Response;
use Symfony\Component\HttpFoundation\StreamedResponse;
use Zouriel\Workflows\Core\Storage\AuditQuery;
use Zouriel\Workflows\Core\Time\Instant;
use Zouriel\Workflows\Core\WorkflowException;
use Zouriel\Workflows\Laravel\Http\ApiJson;
use Zouriel\Workflows\Laravel\Http\AuditCsv;

/** The audit log: query, export (CSV or JSON, streamed), verify the hash chain, purge past retention. */
final class AuditController extends Controller
{
    private const int EXPORT_PAGE = 1000;

    private const int MAX_EXPORT_ROWS = 1_000_000;

    public function index(Request $request): Response
    {
        return ApiJson::response($this->engine->queryAudit(new AuditQuery(
            category: self::queryText($request, 'category'), action: self::queryText($request, 'action'), entity: self::queryText($request, 'entity'),
            entityId: self::queryText($request, 'entityId'), instanceId: self::queryText($request, 'instanceId'),
            definitionId: self::queryText($request, 'definitionId'), actorId: self::queryText($request, 'actorId'),
            from: self::queryInstant($request, 'from'), to: self::queryInstant($request, 'to'), before: self::queryInt($request, 'before'),
            take: self::clamp(self::queryInt($request, 'take'), 100, 1, 1000))));
    }

    /** Streams the audit log page by page as CSV (`format=csv`) or JSON, then records the export (exports are audited too). */
    public function export(Request $request): StreamedResponse
    {
        $csv = self::queryText($request, 'format') === 'csv';
        $category = self::queryText($request, 'category');
        $entity = self::queryText($request, 'entity');
        $entityId = self::queryText($request, 'entityId');
        $from = self::queryInstant($request, 'from');
        $to = self::queryInstant($request, 'to');
        $actor = $this->actor($request);

        return new StreamedResponse(function () use ($csv, $category, $entity, $entityId, $from, $to, $actor) {
            echo $csv ? AuditCsv::HEADER : '[';
            $count = 0;
            for ($next = 1; $count < self::MAX_EXPORT_ROWS;) {
                $page = $this->engine->queryAudit(new AuditQuery(category: $category, entity: $entity, entityId: $entityId, from: $from, to: $to,
                    fromSeq: $next, take: self::EXPORT_PAGE));
                foreach ($page as $record) {
                    echo $csv ? AuditCsv::row($record) : ($count === 0 ? '' : ',').ApiJson::encode(ApiJson::compact($record));
                    $count++;
                }
                flush();
                if (count($page) < self::EXPORT_PAGE) {
                    break;
                }
                $next = $page[count($page) - 1]->seq + 1;
            }
            if (! $csv) {
                echo ']';
            }
            $this->engine->noteAuditExport($actor, $csv ? 'csv' : 'json', $count, ['category' => $category, 'entity' => $entity, 'entityId' => $entityId]);
        }, 200, [
            'Content-Type' => $csv ? 'text/csv' : 'application/json',
            'Content-Disposition' => 'attachment; filename="workflow-audit.'.($csv ? 'csv' : 'json').'"',
        ]);
    }

    public function verify(Request $request): Response
    {
        $result = $this->engine->verifyAudit(self::queryInt($request, 'fromSeq') ?? 1);

        return ApiJson::response((object) [
            'valid' => $result->valid, 'checked' => $result->checked, 'brokenAtSeq' => $result->brokenAtSeq, 'problem' => $result->problem,
            'purgedGaps' => array_map(fn (array $gap) => (object) ['from' => $gap[0], 'to' => $gap[1]], $result->purgedGaps),
        ]);
    }

    /** Purging deletes evidence: it needs the settings gate, not just the right to read the log. */
    public function purge(Request $request): Response
    {
        return ApiJson::response((object) ['purged' => $this->engine->purgeAudit($this->actor($request))]);
    }

    /** A date-time query value (ISO 8601) in milliseconds, or null when absent; anything else is a 400. */
    private static function queryInstant(Request $request, string $name): ?int
    {
        $text = self::queryText($request, $name);

        return $text === null ? null : Instant::parse($text) ?? throw new WorkflowException('invalidInput', "'{$name}' must be a date and time (ISO 8601).");
    }
}
