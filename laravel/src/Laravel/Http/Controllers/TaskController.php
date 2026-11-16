<?php

namespace Zouriel\Workflows\Laravel\Http\Controllers;

use Illuminate\Http\Request;
use Illuminate\Http\Response;
use Zouriel\Workflows\Core\Engine\Views\Page;
use Zouriel\Workflows\Core\Engine\Views\TaskView;
use Zouriel\Workflows\Core\Json\Values;
use Zouriel\Workflows\Core\Storage\TaskQuery;
use Zouriel\Workflows\Core\Storage\TaskStatus;
use Zouriel\Workflows\Core\WorkflowException;
use Zouriel\Workflows\Laravel\Http\ApiErrors;
use Zouriel\Workflows\Laravel\Http\ApiJson;
use Zouriel\Workflows\Laravel\Http\ApiOptions;
use Zouriel\Workflows\Laravel\Http\Middleware\AuthorizeOperation;

/** Tasks: the caller's inbox (or everyone's, for interveners), one task, decide, delegate, reassign. */
final class TaskController extends Controller
{
    /** `scope=all` needs the intervention gate; otherwise the caller's open tasks (any `status` given). Hidden tasks are left out of the total. */
    public function index(Request $request): Response
    {
        $me = $this->actor($request);
        $all = self::queryText($request, 'scope') === 'all';
        if ($all && ! AuthorizeOperation::allows($request, $this->options(), ApiOptions::INTERVENE)) {
            return ApiErrors::error('notAllowed', 'You are not allowed to see everyone\'s tasks.', 403);
        }
        $skip = max(0, self::queryInt($request, 'skip') ?? 0);
        $take = self::clamp(self::queryInt($request, 'take'), 50, 1, 500);
        if (! $all && $me->id === null) {
            return ApiJson::response(new Page([], 0));
        }
        $statuses = self::split(self::queryText($request, 'status')) ?? ($all ? null : [TaskStatus::OPEN]);
        $query = fn (int $s, int $t) => $this->engine->queryTasks(new TaskQuery($all ? null : $me->id, entity: self::queryText($request, 'entity'),
            entityId: self::queryText($request, 'entityId'), statuses: $statuses, skip: $s, take: $t), $me);

        return ApiJson::response($this->visiblePage($query, fn (TaskView $t) => $this->access->canViewTask($me, $t), $skip, $take));
    }

    public function show(Request $request, string $id): Response
    {
        return ApiJson::response($this->visibleTask($request, $id));
    }

    public function decide(Request $request, string $id): Response
    {
        $body = $this->body($request);
        $data = self::answers($body->object('data'));
        $limit = $this->options()->maxDecisionDataBytes;
        if (strlen(Values::serialize($data)) > $limit) {
            throw new WorkflowException('tooLarge', 'The answers are too large (at most '.intdiv($limit, 1024).' KB).');
        }
        $this->visibleTask($request, $id);

        return ApiJson::response($this->engine->decide($id, $this->actor($request), $body->string('outcome') ?? '', $body->string('comment'), $data));
    }

    public function delegate(Request $request, string $id): Response
    {
        $body = $this->body($request);
        $this->visibleTask($request, $id);

        return ApiJson::response($this->engine->delegate($id, $this->actor($request), $body->string('to') ?? '', $body->string('note')));
    }

    public function reassign(Request $request, string $id): Response
    {
        $body = $this->body($request);
        $this->visibleTask($request, $id);

        return ApiJson::response($this->engine->reassign($id, $this->actor($request), $body->strings('assignees') ?? [], $body->string('reason')));
    }
}
