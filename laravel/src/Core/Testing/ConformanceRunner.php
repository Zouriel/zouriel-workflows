<?php

namespace Zouriel\Workflows\Core\Testing;

use Closure;
use stdClass;
use Zouriel\Forms\Json;
use Zouriel\Workflows\Core\Audit\AuditRecord;
use Zouriel\Workflows\Core\Changes\WriteScope;
use Zouriel\Workflows\Core\Engine\Views\DefinitionSummary;
use Zouriel\Workflows\Core\Engine\Views\InstanceView;
use Zouriel\Workflows\Core\Engine\Views\ResetItem;
use Zouriel\Workflows\Core\Engine\Views\TaskView;
use Zouriel\Workflows\Core\Host\WebhookResponse;
use Zouriel\Workflows\Core\Host\WorkflowActor;
use Zouriel\Workflows\Core\Host\WorkflowUser;
use Zouriel\Workflows\Core\Json\Values;
use Zouriel\Workflows\Core\Registry\WorkflowRegistry;
use Zouriel\Workflows\Core\Settings\GlobalSettings;
use Zouriel\Workflows\Core\Storage\BookmarkRecord;
use Zouriel\Workflows\Core\Storage\InstanceKinds;
use Zouriel\Workflows\Core\Storage\InstanceQuery;
use Zouriel\Workflows\Core\Storage\TaskQuery;
use Zouriel\Workflows\Core\Storage\TaskRecord;
use Zouriel\Workflows\Core\Storage\TaskStatus;
use Zouriel\Workflows\Core\Time\Instant;
use Zouriel\Workflows\Core\Time\IsoDuration;
use Zouriel\Workflows\Core\Time\ManualClock;
use Zouriel\Workflows\Core\WorkflowException;

/**
 * Runs a spec conformance fixture (`spec/fixtures/engine/*.json`, format in `spec/fixtures/README.md`) against a fresh
 * `WorkflowTestHost`, throwing `ConformanceException` at the first mismatch. Port of .NET
 * `Zouriel.Workflows.Testing/ConformanceRunner.cs`.
 */
final class ConformanceRunner
{
    /**
     * @param  (Closure(WorkflowRegistry, ManualClock): HostStore)|null  $store  the store to run against (default: a fresh `InMemoryStore`)
     */
    public static function run(string $fixturePath, ?Closure $store = null): void
    {
        $dir = dirname($fixturePath);
        $fx = Json::decode(file_get_contents($fixturePath));
        $file = fn (mixed $value) => is_string($value) ? Json::decode(file_get_contents("{$dir}/{$value}")) : $value;
        $host = new WorkflowTestHost(WorkflowRegistry::fromJson($file($fx->registry)), Instant::parse($fx->now ?? '2026-10-19T09:00:00Z'), $store);
        // Fixtures about failing timers set how many attempts park one; their failures are then expected, not a fault.
        if (isset($fx->maxTimerAttempts)) {
            $host->engine->maxTimerAttempts = (int) $fx->maxTimerAttempts;
            $host->allowTimerFailures = true;
        }
        foreach ($file($fx->users ?? null) ?? [] as $u) {
            $host->identity->add(new WorkflowUser($u->id, $u->name ?? $u->id, $u->email ?? null, $u->roles ?? [], $u->groups ?? [], $u->departments ?? [], $u->portals ?? [],
                $u->queues ?? []), $u->manager ?? null);
        }
        if (($fx->settings ?? null) instanceof stdClass) {
            $host->engine->saveSettings(GlobalSettings::parse($fx->settings), 0, WorkflowActor::user('admin', 'Admin'));
        }
        foreach (Values::members($fx->records ?? null) as $entity => $list) {
            foreach ($list as $r) {
                $values = Json::copy($r);
                $id = Values::text($values->id);
                unset($values->id);
                $host->store->createRecord((string) $entity, $id, $values);
            }
        }
        foreach (Values::members($fx->actions ?? null) as $key => $behaviour) {
            $host->actions->set((string) $key, $behaviour->output ?? null, (int) ($behaviour->failTimes ?? 0), $behaviour->failMessage ?? null);
        }
        foreach (Values::members($fx->webhooks ?? null) as $prefix => $responses) {
            $host->webhooks->responses[(string) $prefix] = array_map(fn ($r) => new WebhookResponse((int) $r->status, property_exists($r, 'body') ? Json::encode($r->body) : null), $responses);
        }
        // Records exist (and their creation is processed) before the workflows are published.
        $host->settle();
        foreach ($fx->definitions ?? [] as $definition) {
            $host->publish(Json::encode($definition));
        }
        $host->settle();
        foreach ($fx->steps ?? [] as $i => $step) {
            $label = 'step '.($i + 1).' ('.implode(',', array_keys(get_object_vars($step))).')';
            try {
                self::runStep($host, $step);
            } catch (ConformanceException $e) {
                throw new ConformanceException(basename($fixturePath)." › {$label}: {$e->getMessage()}", 0, $e);
            }
        }
    }

    private static function actor(WorkflowTestHost $host, mixed $id): WorkflowActor
    {
        if (! is_string($id)) {
            return WorkflowActor::system();
        }
        $user = $host->identity->user($id);

        return WorkflowActor::user($id, $user->name ?? $id, $user?->roles);
    }

    private static function admin(WorkflowTestHost $host): WorkflowActor
    {
        return self::actor($host, 'admin');
    }

    private static function runStep(WorkflowTestHost $host, stdClass $s): void
    {
        $expectError = $s->expectError ?? null;
        try {
            self::act($host, $s);
            if ($expectError !== null) {
                throw new ConformanceException("expected error '{$expectError}' but the step succeeded");
            }
        } catch (WorkflowException $e) {
            if ($expectError === null) {
                throw new ConformanceException("unexpected error '{$e->errorCode}': {$e->getMessage()}");
            }
            if ($e->errorCode !== $expectError) {
                throw new ConformanceException("expected error '{$expectError}' but got '{$e->errorCode}': {$e->getMessage()}");
            }
            $host->settle();
        }
        if (($s->expect ?? null) instanceof stdClass) {
            self::check($host, $s->expect);
        }
    }

    private static function obj(mixed $value): stdClass
    {
        return $value instanceof stdClass ? Json::copy($value) : new stdClass;
    }

    private static function act(WorkflowTestHost $host, stdClass $s): void
    {
        $e = $host->engine;
        if (isset($s->create)) {
            $c = $s->create;
            $host->store->createRecord($c->entity, $c->id, self::obj($c->values ?? null));
        } elseif (isset($s->change)) {
            $ch = $s->change;
            $change = fn () => $host->store->changeRecord($ch->entity, $ch->id, self::obj($ch->values ?? null));
            isset($ch->actor) ? $e->scopes->within(new WriteScope(self::actor($host, $ch->actor), 0), $change) : $change();
        } elseif (isset($s->delete)) {
            $host->store->deleteRecord($s->delete->entity, $s->delete->id);
        } elseif (isset($s->transition)) {
            $tr = $s->transition;
            $e->applyTransition($tr->entity, $tr->id, $tr->transition, self::obj($tr->input ?? null), self::actor($host, $tr->actor ?? null), $tr->lifecycle ?? null,
                $tr->comment ?? null);
        } elseif (isset($s->reset)) {
            $rs = $s->reset;
            $actor = self::actor($host, $rs->actor ?? null);
            // "undo": explicit item ids, or "undoSelected": true for whatever the reset list ticks by default.
            $undo = $rs->undo ?? [];
            if (($rs->undoSelected ?? false) === true) {
                $undo = array_map(fn (ResetItem $i) => $i->id, array_values(array_filter($e->getResetPreview($rs->entity, $rs->id, $actor, $rs->lifecycle ?? null)->items,
                    fn (ResetItem $i) => $i->selected)));
            }
            $e->reset($rs->entity, $rs->id, self::obj($rs->input ?? null), $actor, $rs->lifecycle ?? null, $undo, comment: $rs->comment ?? null);
        } elseif (isset($s->forceState)) {
            $fs = $s->forceState;
            $e->forceState($fs->entity, $fs->id, $fs->state, $fs->reason ?? '', $fs->skipSteps ?? false, self::actor($host, $fs->actor ?? null), $fs->lifecycle ?? null);
        } elseif (isset($s->decide)) {
            $d = $s->decide;
            $task = self::findTask($host, $d->step ?? null, isset($d->task) ? (int) $d->task : null);
            $e->decide($task->id, self::actor($host, $d->user ?? null), $d->outcome, $d->comment ?? null, self::obj($d->data ?? null));
        } elseif (isset($s->delegate)) {
            $dg = $s->delegate;
            $e->delegate(self::findTask($host, $dg->step ?? null, null)->id, self::actor($host, $dg->user ?? null), $dg->to, null);
        } elseif (isset($s->signal)) {
            $sig = $s->signal;
            $e->signal($sig->signal, $sig->entity ?? null, $sig->id ?? null, self::obj($sig->data ?? null));
        } elseif (isset($s->manual)) {
            $m = $s->manual;
            $e->startManual($m->workflow, $m->entity ?? null, $m->id ?? null, self::obj($m->input ?? null), self::actor($host, $m->actor ?? null));
        } elseif (isset($s->advance)) {
            $host->advance(IsoDuration::parse($s->advance)->approximateMs());
        } elseif (isset($s->publish)) {
            $host->publish(Json::encode($s->publish));
        } elseif (isset($s->retry)) {
            $e->retry(self::findInstance($host, $s->retry->definition)->id, self::admin($host), $s->retry->step ?? null);
        } elseif (isset($s->skip)) {
            $e->skip(self::findInstance($host, $s->skip->definition)->id, self::obj($s->skip->outputs ?? null), self::admin($host), $s->skip->step ?? null);
        } elseif (isset($s->cancel)) {
            $e->cancel(self::findInstance($host, $s->cancel->definition)->id, 'test', self::admin($host));
        } elseif (isset($s->migrate)) {
            $mg = $s->migrate;
            $instance = $e->queryInstances(new InstanceQuery(definitionKey: $mg->definition, entityId: $mg->id ?? null, kinds: [InstanceKinds::RECORD, InstanceKinds::AUTOMATION]))->items[0];
            $e->migrate($instance->id, (int) $mg->version, isset($mg->stateMap) ? Values::members($mg->stateMap) : null, self::admin($host));
        } elseif (isset($s->settings)) {
            [, $revision] = $e->getSettings();
            $e->saveSettings(GlobalSettings::parse($s->settings), $revision, self::admin($host));
        } elseif (isset($s->refuse)) {
            self::breakdown($host, $s->refuse, true);
        } elseif (isset($s->allow)) {
            self::breakdown($host, $s->allow, false);
        } elseif (isset($s->archive)) {
            $e->archive(self::definitionId($host, $s->archive), self::admin($host));
        } elseif (isset($s->restore)) {
            $e->restore(self::definitionId($host, $s->restore), self::admin($host));
        } elseif (isset($s->permissions)) {
            $pm = $s->permissions;
            $e->updatePermissions(self::definitionId($host, $pm->workflow), array_map(fn ($c) => [$c->node, $c->who], $pm->changes), $pm->note ?? null, self::admin($host));
        } elseif (! isset($s->expect)) {
            throw new ConformanceException('unknown step: '.Json::encode($s));
        }
        $host->settle();
    }

    /** `refuse` / `allow`: the store refuses writes to `entity.field`, and the directory fails looking up `userLookups`. */
    private static function breakdown(WorkflowTestHost $host, stdClass $spec, bool $on): void
    {
        foreach ($spec->writes ?? [] as $w) {
            $host->store->refuseWrites($w, $on);
        }
        foreach ($spec->userLookups ?? [] as $id) {
            if ($on) {
                $host->identity->broken[$id] = true;
            } else {
                unset($host->identity->broken[$id]);
            }
        }
    }

    private static function definitionId(WorkflowTestHost $host, string $key): string
    {
        return array_find($host->engine->listDefinitions(), fn (DefinitionSummary $d) => $d->key === $key)->id;
    }

    private static function taskNode(WorkflowTestHost $host, string $id): string
    {
        return array_find($host->store->all('tasks'), fn (TaskRecord $r) => $r->id === $id)->nodeId;
    }

    private static function findTask(WorkflowTestHost $host, ?string $step, ?int $index): TaskView
    {
        $all = $host->engine->queryTasks(new TaskQuery(take: 1000), null)->items;
        usort($all, fn (TaskView $a, TaskView $b) => strcmp($a->createdAt, $b->createdAt));
        $tasks = array_values(array_filter($all, fn (TaskView $t) => $step === null || self::taskNode($host, $t->id) === $step));
        if ($index !== null) {
            return $tasks[$index] ?? throw new ConformanceException("no task #{$index} for step '{$step}'");
        }

        // The open task, else the latest one (so deciding a closed task can be tested).
        return array_find($tasks, fn (TaskView $t) => $t->status === TaskStatus::OPEN) ?? ($tasks[count($tasks) - 1] ?? throw new ConformanceException("no task for step '{$step}'"));
    }

    private static function findInstance(WorkflowTestHost $host, string $key): InstanceView
    {
        $list = self::inCreationOrder($host, $host->engine->queryInstances(new InstanceQuery(definitionKey: $key, take: 1000))->items);

        return $list[count($list) - 1] ?? throw new ConformanceException("no instance of '{$key}'");
    }

    /**
     * Instances in the order they were created (the audit sequence breaks ties between equal clock times).
     *
     * @param  list<InstanceView>  $items
     * @return list<InstanceView>
     */
    private static function inCreationOrder(WorkflowTestHost $host, array $items): array
    {
        $first = [];
        $audit = $host->store->all('audit');
        usort($audit, fn (AuditRecord $a, AuditRecord $b) => $a->seq <=> $b->seq);
        foreach ($audit as $a) {
            $key = $a->instanceId ?? ($a->subjectType === 'record' ? null : $a->subjectId);
            if ($key !== null && ! isset($first[$key])) {
                $first[$key] = $a->seq;
            }
        }
        usort($items, fn (InstanceView $a, InstanceView $b) => strcmp($a->startedAt, $b->startedAt) ?: ($first[$a->id] ?? PHP_INT_MAX) <=> ($first[$b->id] ?? PHP_INT_MAX));

        return $items;
    }

    // ── Expectations ──────────────────────────────────────────────────────

    private static function check(WorkflowTestHost $host, stdClass $expect): void
    {
        $e = $host->engine;
        if (isset($expect->instances)) {
            $kinds = $expect->instanceKinds ?? [InstanceKinds::AUTOMATION, InstanceKinds::RECORD];
            $got = self::inCreationOrder($host, $e->queryInstances(new InstanceQuery(kinds: $kinds, take: 10000))->items);
            self::compareList('instances', $expect->instances, array_map(fn (InstanceView $i) => (object) [
                'definition' => $i->definitionKey, 'kind' => $i->kind, 'status' => $i->status, 'state' => $i->state, 'record' => $i->entityId, 'version' => (float) $i->version,
                'error' => $i->error, 'reason' => $i->reason,
            ], $got));
        }
        if (isset($expect->record)) {
            $rec = $expect->record;
            $snapshot = $host->store->peekRecord($rec->entity, $rec->id);
            if (($rec->deleted ?? false) === true) {
                if ($snapshot !== null) {
                    throw new ConformanceException('record: expected it to be deleted');
                }
            } else {
                if ($snapshot === null) {
                    throw new ConformanceException('record: does not exist');
                }
                self::compare('record', $rec->values ?? new stdClass, $snapshot->values);
            }
        }
        if (isset($expect->tasks)) {
            $records = [];
            foreach ($host->store->all('tasks') as $r) {
                $records[$r->id] = $r;
            }
            $created = [];
            foreach ($host->store->all('audit') as $a) {
                if ($a->action === 'created' && ! isset($created[$a->subjectId])) {
                    $created[$a->subjectId] = $a->seq;
                }
            }
            $tasks = $e->queryTasks(new TaskQuery(take: 1000), null)->items;
            usort($tasks, fn (TaskView $a, TaskView $b) => $records[$a->id]->createdAt <=> $records[$b->id]->createdAt ?: ($created[$a->id] ?? 0) <=> ($created[$b->id] ?? 0));
            self::compareList('tasks', $expect->tasks, array_map(fn (TaskView $t) => (object) [
                'step' => $records[$t->id]->nodeId, 'status' => $t->status, 'assignees' => $t->assignees, 'outcome' => $t->outcome, 'title' => $t->title, 'due' => $t->dueAt,
                'instructions' => $t->instructions, 'escalationLevel' => (float) $t->escalationLevel,
            ], $tasks));
        }
        if (isset($expect->notifications)) {
            self::compareList('notifications', $expect->notifications, array_map(fn ($n) => (object) [
                'channel' => $n->channel, 'to' => array_map(fn ($r) => $r->userId ?? $r->email, $n->to), 'subject' => $n->subject, 'body' => $n->body, 'link' => $n->link,
            ], $host->notifications->sent));
        }
        if (isset($expect->notificationCount) && count($host->notifications->sent) !== (int) $expect->notificationCount) {
            throw new ConformanceException("notificationCount: expected {$expect->notificationCount}, got ".count($host->notifications->sent).': '
                .implode(' | ', array_map(fn ($n) => $n->subject, $host->notifications->sent)));
        }
        if (isset($expect->actionsCalled)) {
            self::compareList('actionsCalled', $expect->actionsCalled, array_map(fn ($c) => (object) ['key' => $c->actionKey, 'input' => $c->input, 'attempt' => (float) $c->attempt],
                $host->actions->calls));
        }
        if (isset($expect->webhooks)) {
            self::compareList('webhooks', $expect->webhooks, array_map(fn ($c) => (object) [
                'method' => $c->method, 'url' => $c->url, 'body' => $c->body === null ? null : Json::decode($c->body), 'headers' => Values::object($c->headers),
            ], $host->webhooks->calls));
        }
        foreach (Values::members($expect->outputs ?? null) as $key => $steps) {
            self::compare("outputs.{$key}", $steps, $e->getInstance(self::findInstance($host, (string) $key)->id)->outputs);
        }
        foreach (Values::members($expect->vars ?? null) as $key => $vars) {
            self::compare("vars.{$key}", $vars, $e->getInstance(self::findInstance($host, (string) $key)->id)->variables);
        }
        if (isset($expect->available)) {
            self::checkAvailable($host, $expect->available);
        }
        if (isset($expect->resetPreview)) {
            self::checkResetPreview($host, $expect->resetPreview);
        }
        if (isset($expect->audit)) {
            self::checkAudit($host, $expect->audit);
        }
        foreach (Values::members($expect->auditCount ?? null) as $kind => $count) {
            $n = count(array_filter($host->store->all('audit'), fn (AuditRecord $a) => "{$a->category}.{$a->action}" === $kind));
            if ($n !== (int) $count) {
                throw new ConformanceException("auditCount.{$kind}: expected {$count}, got {$n}");
            }
        }
        if (($expect->auditChainValid ?? false) === true) {
            $result = $e->verifyAudit();
            if (! $result->valid) {
                throw new ConformanceException("audit chain broken at {$result->brokenAtSeq}: {$result->problem}");
            }
        }
        if (isset($expect->bookmarks)) {
            $bookmarks = $host->store->all('bookmarks');
            if (count($bookmarks) !== (int) $expect->bookmarks) {
                throw new ConformanceException("bookmarks: expected {$expect->bookmarks}, got ".count($bookmarks).': '.implode(', ', array_map(fn (BookmarkRecord $b) => $b->purpose, $bookmarks)));
            }
        }
    }

    private static function checkAvailable(WorkflowTestHost $host, stdClass $av): void
    {
        $view = $host->engine->getRecord($av->entity, $av->id, self::actor($host, $av->user ?? null));
        self::compare('available.transitions', $av->transitions ?? [], array_map(fn ($t) => $t->key, $view->transitions));
        if (isset($av->state) && $view->state !== $av->state) {
            throw new ConformanceException("available.state: expected {$av->state}, got {$view->state}");
        }
        if (isset($av->reset) && ($view->reset !== null) !== $av->reset) {
            throw new ConformanceException('available.reset: expected '.Json::encode($av->reset).', got '.Json::encode($view->reset !== null));
        }
        // "comments": { transitionKey: "none"|"optional"|"required" } for the offered transitions; "resetComment" for the reset option.
        if (isset($av->comments)) {
            $comments = new stdClass;
            foreach ($view->transitions as $t) {
                $comments->{$t->key} = $t->comment;
            }
            self::compare('available.comments', $av->comments, $comments);
        }
        // "status" and "error": the record instance's status as the record page shows it, and why it is in incident.
        if (isset($av->status) && $view->status !== $av->status) {
            throw new ConformanceException("available.status: expected {$av->status}, got {$view->status}");
        }
        if (property_exists($av, 'error')) {
            self::compare('available.error', $av->error, $view->error);
        }
        // "publicProcessingTime": the estimate the record view carries (null = the view has none).
        if (property_exists($av, 'publicProcessingTime')) {
            self::compare('available.publicProcessingTime', $av->publicProcessingTime, $view->publicProcessingTime);
        }
        if (isset($av->resetComment) && $view->reset?->comment !== $av->resetComment) {
            throw new ConformanceException("available.resetComment: expected {$av->resetComment}, got ".($view->reset?->comment ?? 'no reset'));
        }
    }

    private static function checkResetPreview(WorkflowTestHost $host, stdClass $rp): void
    {
        $viewer = self::actor($host, $rp->user ?? null);
        // The reset list, item by item: { id, kind?, selected?, undoable? } in order; or { error } when it is refused.
        if (isset($rp->error)) {
            try {
                $host->engine->getResetPreview($rp->entity, $rp->id, $viewer);
            } catch (WorkflowException $ex) {
                if ($ex->errorCode === $rp->error) {
                    return;
                }
                throw new ConformanceException("resetPreview: expected error '{$rp->error}' but got '{$ex->errorCode}'");
            }
            throw new ConformanceException("resetPreview: expected error '{$rp->error}' but it was listed");
        }
        $items = $host->engine->getResetPreview($rp->entity, $rp->id, $viewer)->items;
        if (count($rp->items) !== count($items)) {
            throw new ConformanceException('resetPreview.items: expected '.count($rp->items).', got '.count($items).': '.implode(', ', array_map(fn (ResetItem $i) => $i->id, $items)));
        }
        foreach ($rp->items as $k => $want) {
            $i = $items[$k];
            self::compare("resetPreview.items[{$k}]", $want, (object) [
                'id' => $i->id, 'kind' => $i->kind, 'selected' => $i->selected, 'undoable' => $i->undoable, 'title' => $i->title, 'detail' => $i->detail,
            ]);
        }
    }

    /** A subsequence check: these entries appear in this order (others may be in between). @param  list<mixed>  $want */
    private static function checkAudit(WorkflowTestHost $host, array $want): void
    {
        $entries = $host->store->all('audit');
        usort($entries, fn (AuditRecord $a, AuditRecord $b) => $a->seq <=> $b->seq);
        $pos = 0;
        foreach ($want as $w) {
            $o = is_string($w) ? (object) ['is' => $w] : $w;
            $found = false;
            for (; $pos < count($entries); $pos++) {
                $a = $entries[$pos];
                if ("{$a->category}.{$a->action}" !== $o->is) {
                    continue;
                }
                if (isset($o->data) && ! self::matches($o->data, Values::fromJson($a->data))) {
                    continue;
                }
                if (isset($o->actor) && $a->actorId !== $o->actor && $a->actorType !== $o->actor) {
                    continue;
                }
                $found = true;
                $pos++;
                break;
            }
            if (! $found) {
                throw new ConformanceException('audit: missing \''.Json::encode($o).'\' (in order). Have: '.implode(', ', array_map(fn (AuditRecord $a) => "{$a->category}.{$a->action}", $entries)));
            }
        }
    }

    private static function matches(mixed $want, mixed $actual): bool
    {
        try {
            self::compare('', $want, $actual);

            return true;
        } catch (ConformanceException) {
            return false;
        }
    }

    /** @param  list<mixed>  $want  @param  list<stdClass>  $actual */
    private static function compareList(string $path, array $want, array $actual): void
    {
        if (count($want) !== count($actual)) {
            $compact = array_map(fn (stdClass $o) => (object) array_filter(get_object_vars($o), fn ($v) => $v !== null), $actual);
            throw new ConformanceException("{$path}: expected ".count($want).' item(s), got '.count($actual).': '.Json::encode($compact));
        }
        foreach ($want as $i => $w) {
            self::compare("{$path}[{$i}]", $w, $actual[$i]);
        }
    }

    /** Objects in the expectation are subsets (extra actual keys are fine); lists and scalars must match exactly. */
    private static function compare(string $path, mixed $want, mixed $actual): void
    {
        $actual = Json::normalize($actual);
        if ($want instanceof stdClass) {
            if (! $actual instanceof stdClass) {
                throw new ConformanceException("{$path}: expected an object, got ".Json::encode($actual));
            }
            foreach (get_object_vars($want) as $k => $v) {
                self::compare("{$path}.{$k}", $v, $actual->{$k} ?? null);
            }

            return;
        }
        if (is_array($want)) {
            if (! is_array($actual) || count($actual) !== count($want)) {
                throw new ConformanceException("{$path}: expected ".Json::encode($want).', got '.Json::encode($actual));
            }
            foreach ($want as $i => $w) {
                self::compare("{$path}[{$i}]", $w, $actual[$i]);
            }

            return;
        }
        if (! Values::deepEquals($want, $actual)) {
            throw new ConformanceException("{$path}: expected ".Json::encode($want).', got '.Json::encode($actual));
        }
    }
}
