<?php

namespace Zouriel\Workflows\Laravel\Http\Controllers;

use Illuminate\Http\Request;
use Illuminate\Http\Response;
use Zouriel\Workflows\Core\Engine\Views\InstanceView;
use Zouriel\Workflows\Core\Storage\AuditQuery;
use Zouriel\Workflows\Core\Storage\InstanceKinds;
use Zouriel\Workflows\Core\Storage\InstanceQuery;
use Zouriel\Workflows\Core\Storage\InstanceStatus;
use Zouriel\Workflows\Laravel\Http\ApiJson;

/** Instances: list, read, the version they run, their audit, and the interventions (cancel, retry, skip, migrate). */
final class InstanceController extends Controller
{
    /**
     * Instances the caller may not see are left out of the items and of the total. Without `kind`, automations and record
     * instances are listed; asking for `incident` lists child runs too, so every stuck instance shows in one list.
     */
    public function index(Request $request): Response
    {
        $me = $this->actor($request);
        $statuses = self::split(self::queryText($request, 'status'));
        $kinds = self::split(self::queryText($request, 'kind'))
            ?? ($statuses !== null && in_array(InstanceStatus::INCIDENT, $statuses, true)
                ? [InstanceKinds::AUTOMATION, InstanceKinds::RECORD, InstanceKinds::RUN]
                : [InstanceKinds::AUTOMATION, InstanceKinds::RECORD]);
        $query = fn (int $skip, int $take) => $this->engine->queryInstances(new InstanceQuery(self::queryText($request, 'definitionId'),
            self::queryText($request, 'definitionKey'), self::queryText($request, 'entity'), self::queryText($request, 'entityId'), $statuses, $kinds,
            self::queryText($request, 'parentId'), $skip, $take));

        return ApiJson::response($this->visiblePage($query, fn (InstanceView $i) => $this->access->canViewInstance($me, $i),
            max(0, self::queryInt($request, 'skip') ?? 0), self::clamp(self::queryInt($request, 'take'), 50, 1, 500)));
    }

    public function show(Request $request, string $id): Response
    {
        return ApiJson::response($this->visibleInstance($request, $id));
    }

    public function definition(Request $request, string $id): Response
    {
        $this->visibleInstance($request, $id);

        return ApiJson::response($this->engine->getInstanceDefinition($id));
    }

    public function audit(Request $request, string $id): Response
    {
        return ApiJson::response($this->engine->queryAudit(new AuditQuery(instanceId: $id, fromSeq: 1,
            take: self::clamp(self::queryInt($request, 'take'), 500, 1, 5000))));
    }

    // Interventions act on what the caller could see: the instance must pass the access policy too.

    public function cancel(Request $request, string $id): Response
    {
        $body = $this->optionalBody($request);
        $this->visibleInstance($request, $id);

        return ApiJson::response($this->engine->cancel($id, $body->string('reason') ?? '', $this->actor($request)));
    }

    public function retry(Request $request, string $id): Response
    {
        $body = $this->optionalBody($request);
        $this->visibleInstance($request, $id);

        return ApiJson::response($this->engine->retry($id, $this->actor($request), $body->string('target')));
    }

    public function skip(Request $request, string $id): Response
    {
        $body = $this->optionalBody($request);
        $this->visibleInstance($request, $id);

        return ApiJson::response($this->engine->skip($id, self::answers($body->object('outputs')), $this->actor($request), $body->string('target')));
    }

    public function migrate(Request $request, string $id): Response
    {
        $body = $this->body($request);
        $this->visibleInstance($request, $id);

        return ApiJson::response($this->engine->migrate($id, $body->int('version') ?? 0, $body->stringMap('stateMap'), $this->actor($request)));
    }
}
