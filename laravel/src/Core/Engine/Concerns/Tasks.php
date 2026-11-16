<?php

namespace Zouriel\Workflows\Core\Engine\Concerns;

use stdClass;
use Zouriel\Forms\Js;
use Zouriel\Forms\Json;
use Zouriel\Workflows\Core\Engine\Exec;
use Zouriel\Workflows\Core\Engine\Frame;
use Zouriel\Workflows\Core\Engine\Sod;
use Zouriel\Workflows\Core\Engine\Token;
use Zouriel\Workflows\Core\Engine\Uow;
use Zouriel\Workflows\Core\Engine\Views\TaskDecision;
use Zouriel\Workflows\Core\Engine\Views\TaskView;
use Zouriel\Workflows\Core\Host\NotificationRecipient;
use Zouriel\Workflows\Core\Host\WorkflowActor;
use Zouriel\Workflows\Core\Host\WorkflowNotification;
use Zouriel\Workflows\Core\Json\Values;
use Zouriel\Workflows\Core\Model\ApprovalStep;
use Zouriel\Workflows\Core\Model\HumanStep;
use Zouriel\Workflows\Core\Model\MakerChecker;
use Zouriel\Workflows\Core\Model\Remarks;
use Zouriel\Workflows\Core\Model\TaskOutcome;
use Zouriel\Workflows\Core\Model\TaskStep;
use Zouriel\Workflows\Core\Storage\BookmarkPurposes;
use Zouriel\Workflows\Core\Storage\BookmarkRecord;
use Zouriel\Workflows\Core\Storage\InstanceKinds;
use Zouriel\Workflows\Core\Storage\InstanceRecord;
use Zouriel\Workflows\Core\Storage\TaskQuery;
use Zouriel\Workflows\Core\Storage\TaskRecord;
use Zouriel\Workflows\Core\Storage\TaskStatus;
use Zouriel\Workflows\Core\Time\Instant;
use Zouriel\Workflows\Core\Time\IsoDuration;
use Zouriel\Workflows\Core\WorkflowException;

/** Approvals and tasks: creating, deciding, settling, timing out, reminders, escalations, delegation, reassignment, separation of duties. Port of .NET `Engine/WorkflowEngine.Tasks.cs`. */
trait Tasks
{
    /** The largest decision answers accepted, as JSON (256 KB). */
    public const int MAX_DECISION_DATA_BYTES = 256 * 1024;

    private function createTask(Exec $x, Token $t, HumanStep $h): void
    {
        $scope = $x->scope($t);
        $assignees = $this->resolveAssignees($scope, $h->assignees, null, $x->i->initiatorId);
        $excluded = [];
        if ($h instanceof ApprovalStep && $h->makerChecker?->any() === true) {
            $sod = $this->buildSod($x->u, $h->makerChecker, $x->i, $x->i->kind === InstanceKinds::RUN ? $x->i->parentId : $x->i->id, null, true);
            foreach ($assignees as $id) {
                if ($sod->refused($id) !== null) {
                    $excluded[] = $id;
                }
            }
            $assignees = array_values(array_diff($assignees, $excluded));
        }
        if ($assignees === []) {
            throw new WorkflowException('noAssignees', 'Nobody matches the people chosen for “'.($h->title ?? $h->type).'”.');
        }
        $record = $this->beginStep($x, $t, $h, (object) ['assignees' => $assignees]);
        $due = $this->due($x, $h->due, $h->calendar);
        $title = $h->title ?? ($h instanceof ApprovalStep ? 'Approval' : 'Task');
        $instructions = $h->instructions === null ? null : $this->render($scope, $h->instructions);
        $outcomes = match (true) {
            $h instanceof ApprovalStep => [
                (object) ['key' => 'approved', 'label' => 'Approve', 'tone' => 'success'],
                (object) ['key' => 'rejected', 'label' => 'Reject', 'tone' => 'danger'],
            ],
            $h instanceof TaskStep => array_map(fn (TaskOutcome $o) => (object) ['key' => $o->key, 'label' => $o->label, 'tone' => $o->tone], $h->outcomes),
            default => [],
        };
        $task = new TaskRecord;
        $task->id = self::newId();
        $task->instanceId = $x->i->id;
        $task->rootInstanceId = $x->i->kind === InstanceKinds::RUN ? $x->i->parentId : $x->i->id;
        $task->nodeId = $h->id;
        $task->tokenId = $t->id;
        $task->kind = $h instanceof ApprovalStep ? 'approval' : 'task';
        $task->title = $title;
        $task->instructions = $instructions;
        $task->definitionKey = $x->i->definitionKey;
        $task->entity = $x->i->entity;
        $task->entityId = $x->i->entityId;
        $task->entityTitle = $x->i->entity !== null && $x->i->entityId !== null ? $this->recordTitle($scope, $x->i->entity, $x->i->entityId) : null;
        $task->assignees = Values::serialize($assignees);
        $task->rule = $h instanceof ApprovalStep ? Values::serialize((object) ['type' => $h->rule->type, 'value' => $h->rule->value]) : null;
        $task->outcomes = Values::serialize($outcomes);
        $task->form = $h->form === null ? null : Json::encode($h->form);
        $task->requireComment = $h->requireComment;
        $task->allowDelegation = $h->allowDelegation ?? $x->u->settings->people?->delegation ?? false;
        $task->dueAt = $due;
        $task->createdAt = $x->u->now;
        $x->u->s->addTask($task);
        $this->addTimer($x, $t, $h, BookmarkPurposes::DUE, $due, null, $task->id);
        foreach ($h->reminders ?? $x->u->settings->defaults?->reminders ?? [] as $i => $reminder) {
            $r = IsoDuration::tryParse($reminder);
            if ($r !== null) {
                $this->addTimer($x, $t, $h, BookmarkPurposes::REMINDER, $x->u->calendar->add($x->u->now, $r, $this->isWorking($x, $h->calendar)), (string) $i, $task->id);
            }
        }
        $after = $h->escalation === null ? null : IsoDuration::tryParse($h->escalation->after);
        if ($after !== null) {
            $this->addTimer($x, $t, $h, BookmarkPurposes::ESCALATION, $x->u->calendar->add($x->u->now, $after, $this->isWorking($x, $h->calendar)), null, $task->id);
        }
        self::wait($t, "task:{$task->id}", $record);
        $x->u->s->updateStep($record);
        $data = (object) ['title' => $title, 'assignees' => $assignees, 'due' => Instant::format($due), 'step' => $h->id];
        // `excluded` only when someone was removed, so the audit of workflows without a rule does not change.
        if ($excluded !== []) {
            $data->excluded = $excluded;
        }
        $x->u->audit(self::taskAudit($task, 'created', $x->actor(), $data));
        $x->u->log?->add('task', 'created', $task->id, $title, (object) ['assignees' => $assignees, 'outcomes' => $outcomes]);
        $this->notifyAssignees($x->u, $task, $assignees, "New {$task->kind}: {$title}", $instructions ?? "“{$title}” is waiting for you.", 'created');
    }

    /** @param  list<string>  $userIds */
    private function notifyAssignees(Uow $u, TaskRecord $task, array $userIds, string $subject, string $body, string $why): void
    {
        if (! $this->options->notifyTaskAssignees || $userIds === []) {
            return;
        }
        $channel = $this->options->taskNotificationChannel
            ?? array_find($this->registry->channels, fn ($c) => $c->key === 'inApp')?->key
            ?? array_find($this->registry->channels, fn ($c) => $c->key === 'email')?->key;
        if ($channel === null || ($u->settings->channels[$channel] ?? null)?->enabled === false) {
            return;
        }
        $to = array_map(function (string $id) {
            $user = $this->options->identity->user($id);

            return new NotificationRecipient($id, $user?->email, $user->name ?? $id);
        }, $userIds);
        $this->queueNotification($u, new WorkflowNotification($channel, $to, $task->entityTitle === null ? $subject : "{$subject} ({$task->entityTitle})", $body,
            $this->recordLink($task->entity, $task->entityId), $task->instanceId, $task->entity, $task->entityId, "task:{$task->id}:{$why}:{$task->escalationLevel}"));
    }

    // ── Decisions ─────────────────────────────────────────────────────────

    /** Records a person's decision on an open task and continues the workflow when the task is settled. */
    public function decide(string $taskId, WorkflowActor $user, string $outcome, ?string $comment, ?stdClass $data): TaskView
    {
        if (! $user->isUser()) {
            throw new WorkflowException('notAllowed', 'Only a person can decide a task.');
        }

        return $this->unit(function (Uow $u) use ($taskId, $user, $outcome, $comment, $data) {
            $this->decideCore($u, $taskId, $user, $outcome, $comment, $data);

            return $this->taskView($u, $u->s->task($taskId), $user);
        });
    }

    public function decideCore(Uow $u, string $taskId, WorkflowActor $user, string $outcome, ?string $comment, ?stdClass $data): void
    {
        $task = $u->s->task($taskId) ?? throw new WorkflowException('notFound', 'That task does not exist.');
        if ($task->status !== TaskStatus::OPEN) {
            throw new WorkflowException('taskClosed', 'This task has already been completed.');
        }
        if (! $user->isUser()) {
            throw new WorkflowException('notAllowed', 'Only a person can decide a task.');
        }
        $this->ensureMayJoin($u, $task, $user->id, $user->roles, true);
        $assignees = array_map(Values::text(...), Values::fromJson($task->assignees) ?? []);
        if (! in_array($user->id, $assignees, true)) {
            throw new WorkflowException('notAllowed', 'This task is not assigned to you.');
        }
        $decisions = self::parseDecisions($task);
        if (array_any($decisions, fn (TaskDecision $d) => $d->userId === $user->id)) {
            throw new WorkflowException('alreadyDecided', 'You have already decided this task.');
        }
        $outcomes = array_map(fn ($o) => $o->key ?? null, Values::fromJson($task->outcomes) ?? []);
        if (! in_array($outcome, $outcomes, true)) {
            throw new WorkflowException('invalidOutcome', "'{$outcome}' is not one of this task's choices.");
        }
        $comment = $comment === null || trim($comment) === '' ? null : trim($comment);
        if (self::commentRequired($task, $outcome) && $comment === null) {
            throw new WorkflowException('commentRequired', 'Add a comment to explain your decision.');
        }
        if ($comment !== null && Js::length($comment) > Remarks::MAX_LENGTH) {
            throw new WorkflowException('invalidInput', 'A comment can be at most '.Remarks::MAX_LENGTH.' characters.');
        }
        $data ??= new stdClass;
        if (strlen(Values::serialize($data)) > self::MAX_DECISION_DATA_BYTES) {
            throw new WorkflowException('tooLarge', 'The answers are too large.');
        }
        $data = $this->checkedForm($task->form === null ? null : Json::decode($task->form), $data, $u);
        $decisions[] = new TaskDecision($user->id, $user->name, $outcome, $comment, $data, Instant::format($u->now));
        $task->decisions = Values::serialize(array_map(fn (TaskDecision $d) => $d->toValue(), $decisions));
        $u->audit(self::taskAudit($task, 'decided', $user, (object) ['outcome' => $outcome, 'comment' => $comment, 'answers' => $data]));
        $u->log?->add('task', 'decided', $task->id, ($user->name ?? $user->id).": {$outcome}");
        $this->saveAndSettle($u, $task, $decisions, count($assignees));
    }

    /**
     * Whether choosing `$outcome` needs a comment (spec §7 `requireComment`): always, or for `reject` an approval's
     * `rejected` outcome or a task outcome whose tone is `danger`.
     */
    public static function commentRequired(TaskRecord $task, string $outcome): bool
    {
        if ($task->requireComment === 'always') {
            return true;
        }
        if ($task->requireComment !== 'reject') {
            return false;
        }
        if ($outcome === 'rejected') {
            return true;
        }

        return $task->kind !== 'approval' && array_any(Values::fromJson($task->outcomes) ?? [], fn ($o) => ($o->key ?? null) === $outcome && ($o->tone ?? null) === 'danger');
    }

    /** Saves a task and, when its decisions now settle it (spec §7 rules), completes it and continues the workflow. @param  list<TaskDecision>  $decisions */
    private function saveAndSettle(Uow $u, TaskRecord $task, array $decisions, int $assigneeCount): void
    {
        $settled = self::settle($task, $decisions, $assigneeCount);
        if ($settled === null) {
            $u->s->updateTask($task);

            return;
        }
        $task->status = TaskStatus::COMPLETED;
        $task->outcome = $settled;
        $task->completedAt = $u->now;
        $u->s->updateTask($task);
        $this->continueAfterTask($u, $task, $settled, $decisions);
    }

    /** @return list<TaskDecision> */
    public static function parseDecisions(TaskRecord $task): array
    {
        return array_map(fn ($d) => new TaskDecision(Values::text($d->userId ?? null), is_string($d->name ?? null) ? $d->name : null, Values::text($d->outcome ?? null),
            is_string($d->comment ?? null) ? $d->comment : null, ($d->data ?? null) instanceof stdClass ? $d->data : null, Values::text($d->at ?? null)),
            array_values(array_filter(Values::fromJson($task->decisions) ?? [], fn ($d) => $d instanceof stdClass)));
    }

    /** The settled outcome of a task, or null while more decisions are needed (spec §7 assignee rules). @param  list<TaskDecision>  $decisions */
    public static function settle(TaskRecord $task, array $decisions, int $assigneeCount): ?string
    {
        if ($task->kind !== 'approval') {
            return $decisions === [] ? null : $decisions[count($decisions) - 1]->outcome;
        }
        $rule = Values::fromJson($task->rule ?? '{}');
        $type = is_string($rule->type ?? null) ? $rule->type : 'any';
        $value = is_float($rule->value ?? null) ? (int) $rule->value : 1;
        $approvals = count(array_filter($decisions, fn (TaskDecision $d) => $d->outcome === 'approved'));
        $rejections = count(array_filter($decisions, fn (TaskDecision $d) => $d->outcome === 'rejected'));
        $needed = match ($type) {
            'all' => $assigneeCount,
            'count' => min($value, $assigneeCount),
            'percent' => (int) ceil($assigneeCount * max(1, min(100, $value)) / 100.0),
            default => 1,
        };
        if ($type === 'all' && $rejections > 0) {
            return 'rejected';
        }
        if ($type === 'any' && $approvals > 0) {
            return 'approved';
        }
        if ($type === 'any') {
            return $rejections >= $assigneeCount ? 'rejected' : null;
        }
        if ($approvals >= $needed) {
            return 'approved';
        }

        // Rejected once the remaining people can no longer reach the number needed.
        return $approvals + ($assigneeCount - count($decisions)) < $needed ? 'rejected' : null;
    }

    /** @param  list<TaskDecision>  $decisions */
    private function continueAfterTask(Uow $u, TaskRecord $task, string $outcome, array $decisions): void
    {
        $x = $this->loadExec($u, $task->instanceId);
        $t = $x->s->token($task->tokenId);
        $step = $x->c->step($task->nodeId);
        if ($t === null || ! $step instanceof HumanStep || $t->status !== Token::WAITING) {
            return;
        }
        // Remove the task's timers (due, reminders, escalation).
        foreach ($u->s->bookmarks($x->i->id) as $b) {
            if ($b->taskId === $task->id) {
                $u->s->removeBookmark($b->id);
            }
        }
        $x->s->setOutput($step->id, 'outcome', $outcome);
        $x->s->setOutput($step->id, 'decidedBy', array_map(fn (TaskDecision $d) => $d->userId, $decisions));
        $last = $decisions === [] ? null : $decisions[count($decisions) - 1];
        if ($step instanceof ApprovalStep) {
            $x->s->setOutput($step->id, 'comments', array_values(array_map(fn (TaskDecision $d) => ($d->name ?? $d->userId).": {$d->comment}",
                array_filter($decisions, fn (TaskDecision $d) => $d->comment !== null))));
        } else {
            $x->s->setOutput($step->id, 'comment', $last?->comment);
        }
        $x->s->setOutput($step->id, 'data', $last?->data);
        self::resume($t);
        $this->completeWaitingStep($x, $t, $step);
        $t->frames[] = new Frame(! $step instanceof ApprovalStep && $outcome === 'timedOut' ? "{$step->id}/onTimeout" : "{$step->id}/outcome:{$outcome}");
        $this->drive($x);
    }

    /** A task's due time passed before it was settled. */
    private function taskTimedOut(Uow $u, BookmarkRecord $b): void
    {
        $task = $b->taskId === null ? null : $u->s->task($b->taskId);
        if ($task === null || $task->status !== TaskStatus::OPEN) {
            return;
        }
        $task->status = TaskStatus::TIMED_OUT;
        $task->outcome = 'timedOut';
        $task->completedAt = $u->now;
        $u->s->updateTask($task);
        $u->audit(self::taskAudit($task, 'timedOut', WorkflowActor::system()));
        $u->log?->add('task', 'timedOut', $task->id, $task->title);
        $this->continueAfterTask($u, $task, 'timedOut', self::parseDecisions($task));
    }

    private function taskReminder(Uow $u, BookmarkRecord $b): void
    {
        $task = $b->taskId === null ? null : $u->s->task($b->taskId);
        if ($task === null || $task->status !== TaskStatus::OPEN) {
            return;
        }
        $decided = array_map(fn (TaskDecision $d) => $d->userId, self::parseDecisions($task));
        $waitingOn = array_values(array_filter(array_map(Values::text(...), Values::fromJson($task->assignees) ?? []), fn ($id) => ! in_array($id, $decided, true)));
        $u->audit(self::taskAudit($task, 'reminded', WorkflowActor::system(), (object) ['to' => $waitingOn]));
        $u->log?->add('task', 'reminded', $task->id, $task->title);
        $this->notifyAssignees($u, $task, $waitingOn, "Reminder: {$task->title}",
            "“{$task->title}” is still waiting for you. It is due ".Values::formatForPeople($task->dueAt, $u->settings->zone()).'.', "reminder{$b->payload}");
    }

    private function taskEscalation(Uow $u, BookmarkRecord $b): void
    {
        $task = $b->taskId === null ? null : $u->s->task($b->taskId);
        if ($task === null || $task->status !== TaskStatus::OPEN) {
            return;
        }
        $x = $this->loadExec($u, $task->instanceId);
        $step = $x->c->step($task->nodeId);
        if (! $step instanceof HumanStep || $step->escalation === null) {
            return;
        }
        $current = array_map(Values::text(...), Values::fromJson($task->assignees) ?? []);
        $added = $this->resolveAssignees($x->scope($x->s->token($task->tokenId)), $step->escalation->assignees, $current, $x->i->initiatorId);
        $fresh = array_values(array_filter($added, fn ($a) => ! in_array($a, $current, true)));
        if ($step instanceof ApprovalStep && $step->makerChecker?->any() === true) {
            // Escalation never adds someone separation of duties refuses, and says nothing about it.
            $sod = $this->buildSod($u, $step->makerChecker, $x->i, $task->rootInstanceId, $task, true);
            $fresh = array_values(array_filter($fresh, fn ($id) => $sod->refused($id) === null));
        }
        $task->assignees = Values::serialize([...$current, ...$fresh]);
        $task->escalationLevel++;
        $u->s->updateTask($task);
        $u->audit(self::taskAudit($task, 'escalated', WorkflowActor::system(), (object) ['added' => $fresh, 'step' => $step->id]));
        $u->log?->add('task', 'escalated', $task->id, $task->title);
        if ($step->escalation->notify !== false) {
            $this->notifyAssignees($u, $task, $fresh, "Escalated to you: {$task->title}", "“{$task->title}” is overdue and has been passed to you.", 'escalated');
        }
    }

    /** Hands an open task from one assignee to someone else (when the task allows delegation). */
    public function delegate(string $taskId, WorkflowActor $user, string $toUserId, ?string $note): TaskView
    {
        return $this->unit(function (Uow $u) use ($taskId, $user, $toUserId, $note) {
            $task = $u->s->task($taskId) ?? throw new WorkflowException('notFound', 'That task does not exist.');
            if ($task->status !== TaskStatus::OPEN) {
                throw new WorkflowException('taskClosed', 'This task has already been completed.');
            }
            if (! $task->allowDelegation) {
                throw new WorkflowException('notAllowed', 'This task cannot be handed to someone else.');
            }
            $list = array_map(Values::text(...), Values::fromJson($task->assignees) ?? []);
            $position = array_search($user->id, $list, true);
            if ($position === false) {
                throw new WorkflowException('notAllowed', 'This task is not assigned to you.');
            }
            $decisions = self::parseDecisions($task);
            if (array_any($decisions, fn (TaskDecision $d) => $d->userId === $user->id)) {
                throw new WorkflowException('alreadyDecided', 'You have already decided this task.');
            }
            // Handing it to someone already on it (or who already decided) would quietly lower what the rule needs.
            if (in_array($toUserId, $list, true) || array_any($decisions, fn (TaskDecision $d) => $d->userId === $toUserId)) {
                throw new WorkflowException('alreadyAssigned', 'That person is already on this task.');
            }
            if ($this->options->identity->user($toUserId) === null) {
                throw new WorkflowException('unknownUser', 'That person does not exist.');
            }
            $this->ensureMayJoin($u, $task, $toUserId, null, false);
            $list[$position] = $toUserId;
            $task->assignees = Values::serialize($list);
            $u->audit(self::taskAudit($task, 'delegated', $user, (object) ['from' => $user->id, 'to' => $toUserId, 'note' => $note]));
            $this->saveAndSettle($u, $task, $decisions, count($list));
            $this->notifyAssignees($u, $task, [$toUserId], "Handed to you: {$task->title}", $note ?? ($user->name ?? $user->id)." passed “{$task->title}” to you.",
                "delegated:{$user->id}");

            return $this->taskView($u, $task, $user);
        });
    }

    /** An admin replaces the assignees of an open task. @param  list<string>  $userIds */
    public function reassign(string $taskId, WorkflowActor $admin, array $userIds, ?string $reason): TaskView
    {
        return $this->unit(function (Uow $u) use ($taskId, $admin, $userIds, $reason) {
            $task = $u->s->task($taskId) ?? throw new WorkflowException('notFound', 'That task does not exist.');
            if ($task->status !== TaskStatus::OPEN) {
                throw new WorkflowException('taskClosed', 'This task has already been completed.');
            }
            if ($userIds === []) {
                throw new WorkflowException('noAssignees', 'Choose at least one person.');
            }
            foreach ($userIds as $id) {
                if ($this->options->identity->user($id) === null) {
                    throw new WorkflowException('unknownUser', "'{$id}' is not a person the app knows.");
                }
            }
            foreach ($userIds as $id) {
                $this->ensureMayJoin($u, $task, $id, null, false);
            }
            $before = Values::fromJson($task->assignees);
            $decisions = self::parseDecisions($task);
            $assignees = array_values(array_unique([...$userIds, ...array_map(fn (TaskDecision $d) => $d->userId, $decisions)]));
            $task->assignees = Values::serialize($assignees);
            $entry = self::taskAudit($task, 'reassigned', $admin, (object) ['reason' => $reason]);
            $entry->before = $before;
            $entry->after = Values::fromJson($task->assignees);
            $u->audit($entry);
            $this->saveAndSettle($u, $task, $decisions, count($assignees));
            $this->notifyAssignees($u, $task, $userIds, "Assigned to you: {$task->title}", $reason ?? "“{$task->title}” has been assigned to you.", "reassigned:{$u->now}");

            return $this->taskView($u, $task, $admin);
        });
    }

    // ── Separation of duties (spec §7 makerChecker) ───────────────────────

    /**
     * Throws `segregationOfDuties` when the approval's `makerChecker` rule refuses `$userId` (deciding, being handed
     * the task, or being assigned it by an admin). Tasks and approvals without a rule pass.
     *
     * @param  list<string>|null  $actorRoles
     */
    private function ensureMayJoin(Uow $u, TaskRecord $task, string $userId, ?array $actorRoles, bool $self): void
    {
        if ($task->kind !== 'approval') {
            return;
        }
        $instance = $u->s->instance($task->instanceId);
        if ($instance === null) {
            return;
        }
        $step = $this->compiled($u, $instance->definitionId, $instance->version)->step($task->nodeId);
        if (! $step instanceof ApprovalStep || $step->makerChecker?->any() !== true) {
            return;
        }
        $reason = $this->buildSod($u, $step->makerChecker, $instance, $task->rootInstanceId, $task, $self)->refused($userId, $actorRoles);
        if ($reason === null) {
            return;
        }
        $who = $self ? 'You cannot' : ($this->options->identity->user($userId)?->name ?? $userId).' cannot';
        throw new WorkflowException('segregationOfDuties', "{$who} approve this: {$reason}.");
    }

    /** The people an approval's `makerChecker` rule refuses, worked out from the step and the store. */
    private function buildSod(Uow $u, MakerChecker $mc, InstanceRecord $instance, ?string $rootInstanceId, ?TaskRecord $thisTask, bool $self): Sod
    {
        $sod = new Sod;
        if ($mc->excludeInitiator === true) {
            $reason = $self ? 'the person who started it cannot approve it' : 'they started it';
            if ($instance->initiatorId !== null) {
                $sod->exclude($instance->initiatorId, $reason);
            }
            if ($instance->entity !== null && $instance->entityId !== null && ($ri = $this->recordInitiator($u, $instance->entity, $instance->entityId)) !== null) {
                $sod->exclude($ri, $reason);
            }
        }
        if ($mc->excludePreviousApprover === true && $rootInstanceId !== null) {
            // Only approvals settled before this one was created: one settled later (a parallel path) is not "the step before".
            $previous = array_values(array_filter($u->s->queryTasks(new TaskQuery(rootInstanceId: $rootInstanceId, statuses: [TaskStatus::COMPLETED], take: 1000)),
                fn (TaskRecord $t) => $t->kind === 'approval' && $t->id !== $thisTask?->id && ($thisTask === null || $t->completedAt <= $thisTask->createdAt)));
            usort($previous, fn (TaskRecord $a, TaskRecord $b) => [$b->completedAt, $b->createdAt] <=> [$a->completedAt, $a->createdAt]);
            foreach ($previous === [] ? [] : self::parseDecisions($previous[0]) as $d) {
                $sod->exclude($d->userId, $self ? 'you approved the step before it' : 'they approved the step before it');
            }
        }
        if ($mc->requiredRole !== null && $mc->requiredRole !== '') {
            $sod->requiredRole = $mc->requiredRole;
            $sod->roleLabel = array_find($this->registry->roles, fn ($r) => $r->key === $mc->requiredRole)?->label ?? $mc->requiredRole;
            $sod->roleHolders = array_fill_keys($this->options->identity->usersInRole($mc->requiredRole), true);
        }

        return $sod;
    }
}
