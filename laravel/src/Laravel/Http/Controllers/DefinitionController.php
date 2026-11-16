<?php

namespace Zouriel\Workflows\Laravel\Http\Controllers;

use Illuminate\Http\Request;
use Illuminate\Http\Response;
use JsonException;
use stdClass;
use Zouriel\Workflows\Core\Engine\SimulationEvent;
use Zouriel\Workflows\Core\Engine\SimulationRequest;
use Zouriel\Workflows\Core\Engine\WorkflowEngine;
use Zouriel\Workflows\Core\Json\Values;
use Zouriel\Workflows\Core\WorkflowException;
use Zouriel\Workflows\Laravel\Http\ApiJson;
use Zouriel\Workflows\Laravel\Http\RequestBody;

/** Definitions: list, create, drafts, delete, publish, permissions, archive, restore, rollback, versions, diff; `/validate`, `/simulate`. */
final class DefinitionController extends Controller
{
    public function index(): Response
    {
        return ApiJson::response($this->engine->listDefinitions());
    }

    public function store(Request $request): Response
    {
        return ApiJson::response($this->engine->createDefinition($this->jsonText($request), $this->actor($request)));
    }

    public function show(string $id): Response
    {
        return ApiJson::response($this->engine->getDefinition($id));
    }

    public function update(Request $request, string $id): Response
    {
        return ApiJson::response($this->engine->saveDraft($id, $this->jsonText($request), $this->actor($request)));
    }

    public function destroy(Request $request, string $id): Response
    {
        $this->engine->deleteDefinition($id, $this->actor($request));

        return new Response('', 204);
    }

    public function publish(Request $request, string $id): Response
    {
        return ApiJson::response($this->engine->publish($id, $this->optionalBody($request)->string('note'), $this->actor($request)));
    }

    /** Changing who may do steps publishes straight away (only those changes; drafts keep their edits). */
    public function permissions(Request $request, string $id): Response
    {
        $body = $this->body($request);
        $changes = array_map(function (RequestBody $change) {
            $who = $change->value('who');
            if ($who !== null && ! is_array($who)) {
                throw new JsonException("'who' must be a list.");
            }

            return [$change->string('node') ?? '', $who ?? []];
        }, $body->objects('changes') ?? []);

        return ApiJson::response($this->engine->updatePermissions($id, $changes, $body->string('note'), $this->actor($request)));
    }

    public function archive(Request $request, string $id): Response
    {
        return ApiJson::response($this->engine->archive($id, $this->actor($request)));
    }

    public function restore(Request $request, string $id): Response
    {
        return ApiJson::response($this->engine->restore($id, $this->actor($request)));
    }

    public function rollback(Request $request, string $id): Response
    {
        return ApiJson::response($this->engine->rollback($id, $this->body($request)->int('version') ?? 0, $this->actor($request)));
    }

    public function version(string $id, string $version): Response
    {
        return ApiJson::response($this->engine->getVersion($id, (int) $version));
    }

    public function diff(string $id): Response
    {
        return ApiJson::response($this->engine->diffDraft($id));
    }

    public function validate(Request $request): Response
    {
        return ApiJson::response($this->engine->validate($this->jsonText($request)));
    }

    /** A test run. It reads the real record it starts from, so the caller must be allowed to see it. */
    public function simulate(Request $request): Response
    {
        $body = $this->body($request);
        $definition = $body->value('definition');
        $simulation = self::simulationRequest($body->nested('request'));
        $limit = min($this->options()->maxSimulationEvents, WorkflowEngine::MAX_SIMULATION_EVENTS);
        if (count($simulation->events) > $limit) {
            throw new WorkflowException('tooManyEvents', "A test run can replay at most {$limit} events.");
        }
        if ($simulation->entityId !== null && $definition instanceof stdClass && is_string($definition->entity ?? null)) {
            $this->requireRecord($request, $definition->entity, $simulation->entityId);
        }

        return ApiJson::response($this->engine->simulate(Values::serialize($definition), $simulation, $this->actor($request)));
    }

    /** The record, start and events of a test run (.NET `SimulationRequest`); none given is a run with defaults. */
    private static function simulationRequest(?RequestBody $r): SimulationRequest
    {
        if ($r === null) {
            return new SimulationRequest;
        }

        return new SimulationRequest($r->string('entityId'), $r->object('values'), $r->object('trigger'), $r->string('fromState'), $r->string('transition'),
            $r->object('input'), array_map(fn (RequestBody $e) => new SimulationEvent($e->string('type') ?? '', $e->string('step'), $e->string('outcome'),
                $e->string('comment'), $e->object('data'), $e->string('user'), $e->string('duration'), $e->string('signal'), $e->object('values')),
                $r->objects('events') ?? []));
    }
}
