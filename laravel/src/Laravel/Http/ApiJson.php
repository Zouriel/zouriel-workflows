<?php

namespace Zouriel\Workflows\Laravel\Http;

use Illuminate\Http\Response;
use stdClass;
use Zouriel\Forms\Json;
use Zouriel\Workflows\Core\Audit\AuditRecord;
use Zouriel\Workflows\Core\Engine\Views\DefinitionSummary;
use Zouriel\Workflows\Core\Engine\Views\DefinitionVersionInfo;
use Zouriel\Workflows\Core\Engine\Views\PermissionsSummary;
use Zouriel\Workflows\Core\Time\Instant;

/**
 * Writes API bodies the way .NET's `WorkflowApi` does. Views go out with ASP.NET's web JSON defaults: camelCase
 * members in declaration order, null members included. Error details and the JSON audit export use the workflow
 * document options instead (`Definitions.JsonOptions`): null members of views are left out. Values that are JSON
 * already (`stdClass`, definitions, answers) are written as they are, nulls and all. Members .NET types as
 * `DateTimeOffset` (definition `updatedAt`, version `publishedAt`, audit `at`) are written the way System.Text.Json
 * writes them (`2026-10-19T09:00:00.5+00:00`), the rest of the times are the engine's `…ss.fffZ` text.
 */
final class ApiJson
{
    /** A 200 (or `$status`) response with `$value` as JSON. */
    public static function response(mixed $value, int $status = 200): Response
    {
        return new Response(self::encode(self::value($value)), $status, ['Content-Type' => 'application/json; charset=utf-8']);
    }

    public static function encode(mixed $tree): string
    {
        return Json::encode($tree);
    }

    /** `$value` as a JSON tree with every member (ASP.NET web defaults). */
    public static function value(mixed $value): mixed
    {
        return self::convert($value, true);
    }

    /** `$value` as a JSON tree without the null members of views (`Definitions.JsonOptions`). */
    public static function compact(mixed $value): mixed
    {
        return self::convert($value, false);
    }

    /** A .NET `DateTimeOffset` as System.Text.Json writes it at offset zero: seconds, then only the fraction digits needed. */
    public static function dateTimeOffset(string $instant): string
    {
        $text = Instant::format(Instant::parse($instant));
        $fraction = rtrim(substr($text, 20, 3), '0');

        return substr($text, 0, 19).($fraction === '' ? '' : ".{$fraction}").'+00:00';
    }

    private static function convert(mixed $value, bool $nulls): mixed
    {
        return match (true) {
            $value === null, is_scalar($value) => $value,
            $value instanceof stdClass => $value,
            is_array($value) && array_is_list($value) => array_map(fn ($item) => self::convert($item, $nulls), $value),
            is_array($value) => self::members($value, $nulls, false),
            $value instanceof PermissionsSummary => self::members([
                'changed' => $value->changed, 'draftSkipped' => $value->draftSkipped, 'recordsMoved' => $value->recordsMoved,
                'recordsKept' => $value->recordsKept, ...self::summary($value->summary)], $nulls),
            $value instanceof DefinitionSummary => self::members(self::summary($value), $nulls),
            $value instanceof DefinitionVersionInfo => self::members([...get_object_vars($value), 'publishedAt' => self::dateTimeOffset($value->publishedAt)], $nulls),
            $value instanceof AuditRecord => self::members(self::audit($value), $nulls),
            is_object($value) => self::members(get_object_vars($value), $nulls),
            default => $value,
        };
    }

    /** @return array<string, mixed> */
    private static function summary(DefinitionSummary $summary): array
    {
        return [...get_object_vars($summary), 'updatedAt' => self::dateTimeOffset($summary->updatedAt)];
    }

    /** @return array<string, mixed> an audit entry's members in .NET `AuditRecord` order */
    private static function audit(AuditRecord $r): array
    {
        return [
            'seq' => $r->seq, 'id' => $r->id, 'at' => self::dateTimeOffset($r->at), 'tenant' => $r->tenant, 'actorType' => $r->actorType,
            'actorId' => $r->actorId, 'actorName' => $r->actorName, 'category' => $r->category, 'action' => $r->action,
            'subjectType' => $r->subjectType, 'subjectId' => $r->subjectId, 'entity' => $r->entity, 'entityId' => $r->entityId,
            'instanceId' => $r->instanceId, 'definitionId' => $r->definitionId, 'before' => $r->before, 'after' => $r->after, 'data' => $r->data,
            'correlationId' => $r->correlationId, 'causationId' => $r->causationId, 'prevHash' => $r->prevHash, 'hash' => $r->hash,
        ];
    }

    /** @param  array<string, mixed>  $members */
    private static function members(array $members, bool $nulls, bool $view = true): stdClass
    {
        $out = new stdClass;
        foreach ($members as $key => $item) {
            if ($item === null && ! $nulls && $view) {
                continue;
            }
            $out->{$key} = self::convert($item, $nulls);
        }

        return $out;
    }
}
