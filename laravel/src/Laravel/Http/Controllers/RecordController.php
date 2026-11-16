<?php

namespace Zouriel\Workflows\Laravel\Http\Controllers;

use Illuminate\Http\Request;
use Illuminate\Http\Response;
use Zouriel\Workflows\Core\Audit\AuditRecord;
use Zouriel\Workflows\Core\Storage\AuditQuery;
use Zouriel\Workflows\Laravel\Http\ApiJson;

/** Records (view, history, transitions, reset, force state, manual starts), manual starts about no record, and signals. */
final class RecordController extends Controller
{
    public function show(Request $request, string $entity, string $id): Response
    {
        $this->requireRecord($request, $entity, $id);

        return ApiJson::response($this->engine->getRecord($entity, $id, $this->actor($request)));
    }

    /** What a record page shows as history: its lifecycle entries and task decisions, newest first. */
    public function history(Request $request, string $entity, string $id): Response
    {
        $this->requireRecord($request, $entity, $id);
        $take = self::clamp(self::queryInt($request, 'take'), 200, 1, 2000);
        $entries = [
            ...$this->engine->queryAudit(new AuditQuery(category: 'lifecycle', entity: $entity, entityId: $id, take: $take)),
            ...$this->engine->queryAudit(new AuditQuery(category: 'task', action: 'decided', entity: $entity, entityId: $id, take: $take)),
        ];
        usort($entries, fn (AuditRecord $a, AuditRecord $b) => $b->seq <=> $a->seq);

        return ApiJson::response(array_slice($entries, 0, $take));
    }

    public function transition(Request $request, string $entity, string $id, string $key): Response
    {
        $body = $this->optionalBody($request);
        $this->requireRecord($request, $entity, $id);

        return ApiJson::response($this->engine->applyTransition($entity, $id, $key, self::answers($body->object('input')), $this->actor($request),
            $body->string('lifecycle'), $body->string('comment')));
    }

    /** What a reset would take back (spec §6), for the people the lifecycle's reset `who` allows. */
    public function resetPreview(Request $request, string $entity, string $id): Response
    {
        $this->requireRecord($request, $entity, $id);

        return ApiJson::response($this->engine->getResetPreview($entity, $id, $this->actor($request), self::queryText($request, 'lifecycle')));
    }

    public function reset(Request $request, string $entity, string $id): Response
    {
        $body = $this->optionalBody($request);
        $this->requireRecord($request, $entity, $id);

        return ApiJson::response($this->engine->reset($entity, $id, self::answers($body->object('input')), $this->actor($request), $body->string('lifecycle'),
            $body->strings('undo') ?? [], $body->string('token'), $body->string('comment')));
    }

    public function forceState(Request $request, string $entity, string $id): Response
    {
        $body = $this->body($request);
        $this->requireRecord($request, $entity, $id);

        return ApiJson::response($this->engine->forceState($entity, $id, $body->string('state') ?? '', $body->string('reason') ?? '', $body->bool('skipSteps') ?? false,
            $this->actor($request), $body->string('lifecycle')));
    }

    public function manual(Request $request, string $entity, string $id, string $workflow): Response
    {
        $body = $this->optionalBody($request);
        $this->requireRecord($request, $entity, $id);

        return ApiJson::response($this->engine->startManual($workflow, $entity, $id, self::answers($body->object('input')), $this->actor($request)));
    }

    public function manualWithoutRecord(Request $request, string $workflow): Response
    {
        return ApiJson::response($this->engine->startManual($workflow, null, null, self::answers($this->optionalBody($request)->object('input')),
            $this->actor($request)));
    }

    public function signal(Request $request, string $key): Response
    {
        $body = $this->optionalBody($request);
        $entity = $body->string('entity');
        $entityId = $body->string('entityId');
        if ($entity !== null && $entityId !== null) {
            $this->requireRecord($request, $entity, $entityId);
        }

        return ApiJson::response((object) ['resumed' => $this->engine->signal($key, $entity, $entityId, self::answers($body->object('data')), $this->actor($request))]);
    }
}
