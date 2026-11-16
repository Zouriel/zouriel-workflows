<?php

namespace Zouriel\Workflows\Core\Engine\Concerns;

use JsonException;
use stdClass;
use Zouriel\Forms\Json;
use Zouriel\Workflows\Core\Audit\AuditEntry;
use Zouriel\Workflows\Core\Engine\CompiledDefinition;
use Zouriel\Workflows\Core\Engine\Uow;
use Zouriel\Workflows\Core\Engine\Views\DefinitionDetail;
use Zouriel\Workflows\Core\Engine\Views\DefinitionSummary;
use Zouriel\Workflows\Core\Engine\Views\DefinitionVersionInfo;
use Zouriel\Workflows\Core\Engine\Views\PermissionsSummary;
use Zouriel\Workflows\Core\Host\WorkflowActor;
use Zouriel\Workflows\Core\Json\Canonical;
use Zouriel\Workflows\Core\Lint\DefinitionDiff;
use Zouriel\Workflows\Core\Lint\LintContext;
use Zouriel\Workflows\Core\Lint\Linter;
use Zouriel\Workflows\Core\Lint\LintProblem;
use Zouriel\Workflows\Core\Model\ApplyTransitionStep;
use Zouriel\Workflows\Core\Model\Definitions as DefinitionJson;
use Zouriel\Workflows\Core\Model\State;
use Zouriel\Workflows\Core\Model\Transition;
use Zouriel\Workflows\Core\Model\TriggerTypes;
use Zouriel\Workflows\Core\Model\WorkflowDefinition;
use Zouriel\Workflows\Core\Model\WorkflowKinds;
use Zouriel\Workflows\Core\Storage\BookmarkKinds;
use Zouriel\Workflows\Core\Storage\DefinitionRecord;
use Zouriel\Workflows\Core\Storage\DefinitionStatus;
use Zouriel\Workflows\Core\Storage\DefinitionVersionRecord;
use Zouriel\Workflows\Core\Storage\InstanceKinds;
use Zouriel\Workflows\Core\Storage\InstanceQuery;
use Zouriel\Workflows\Core\Storage\InstanceStatus;
use Zouriel\Workflows\Core\Storage\TriggerRecord;
use Zouriel\Workflows\Core\Time\Cron;
use Zouriel\Workflows\Core\Time\Instant;
use Zouriel\Workflows\Core\WorkflowException;

/** Definitions: create, drafts, lint, publish, versions, rollback, archive, restore, permissions. Port of .NET `Engine/WorkflowEngine.Definitions.cs`. */
trait Definitions
{
    /** @return list<LintProblem> a definition checked against the spec, the registry, settings and the other published workflows */
    public function validate(string $json): array
    {
        return $this->unit(fn (Uow $u) => $this->lint($u, $json));
    }

    /** @return list<LintProblem> */
    public function lint(Uow $u, string $json): array
    {
        try {
            $definition = DefinitionJson::parse($json);
        } catch (JsonException $e) {
            return [new LintProblem('invalidJson', 'error', null, "The definition is not valid JSON: {$e->getMessage()}")];
        }
        $others = [];
        foreach ($u->s->definitions() as $d) {
            if ($d->id === $definition->id || $d->version === 0 || $d->status === DefinitionStatus::ARCHIVED) {
                continue;
            }
            $others[] = $this->compiled($u, $d->id, $d->version)->definition;
        }

        return [...Linter::lint($definition, new LintContext($this->registry, $u->settings, $others)), ...$this->renamedInUse($u, $definition, $others)];
    }

    /**
     * `renamedInUse` warnings (spec §1): a lifecycle state or transition whose key differs from the published version's
     * while records are in the state, or other published workflows name it by its old key.
     *
     * @param  list<WorkflowDefinition>  $others
     * @return list<LintProblem>
     */
    private function renamedInUse(Uow $u, WorkflowDefinition $definition, array $others): array
    {
        $found = [];
        $record = $u->s->definition($definition->id);
        if (! $definition->isLifecycle() || $definition->lifecycle === null || $record === null || $record->version === 0) {
            return $found;
        }
        $published = $this->compiled($u, $definition->id, $record->version)->definition->lifecycle;
        if ($published === null) {
            return $found;
        }
        $renamedStates = [];
        foreach ($definition->lifecycle->states as $s) {
            $old = array_find($published->states, fn (State $o) => $o->id === $s->id);
            if ($old !== null && $old->key !== $s->key) {
                $renamedStates[] = [$s, $old];
            }
        }
        $renamedTransitions = [];
        foreach ($definition->lifecycle->transitions as $t) {
            $old = array_find($published->transitions, fn (Transition $o) => $o->id === $t->id);
            if ($old !== null && $old->key !== $t->key) {
                $renamedTransitions[] = [$t, $old];
            }
        }
        if ($renamedStates === [] && $renamedTransitions === []) {
            return $found;
        }
        $inState = [];
        if ($renamedStates !== []) {
            for ($skip = 0; ; $skip += 1000) {
                $page = $u->s->queryInstances(new InstanceQuery(definitionId: $definition->id, kinds: [InstanceKinds::RECORD], statuses: self::LIVE_RECORD, skip: $skip, take: 1000));
                foreach ($page as $i) {
                    if ($i->state !== null) {
                        $inState[$i->state] = ($inState[$i->state] ?? 0) + 1;
                    }
                }
                if (count($page) < 1000) {
                    break;
                }
            }
        }
        $steps = [];
        foreach ($others as $o) {
            foreach ((new CompiledDefinition($o, $o->id, 0))->lists as $list) {
                foreach ($list as $step) {
                    if ($step instanceof ApplyTransitionStep && $step->lifecycle === $definition->key) {
                        $steps[] = $step;
                    }
                }
            }
        }
        $triggers = array_values(array_filter(array_map(fn (WorkflowDefinition $o) => $o->trigger, $others), fn ($t) => $t?->lifecycle === $definition->key));
        foreach ($renamedStates as [$s, $old]) {
            $records = $inState[$s->id] ?? 0;
            $usedBy = count(array_filter($triggers, fn ($t) => $t->type === TriggerTypes::STATE_ENTERED && in_array($old->key, $t->states ?? [], true)));
            if ($records > 0 || $usedBy > 0) {
                $found[] = new LintProblem('renamedInUse', 'warning', $s->id, "The state '{$old->key}' is renamed to '{$s->key}' while "
                    .($records > 0 ? "{$records} record(s) are in it (their field still holds '{$old->key}'; migrate them after publishing)" : '')
                    .($records > 0 && $usedBy > 0 ? ' and ' : '')
                    .($usedBy > 0 ? "{$usedBy} other workflow(s) start when it is entered" : '').'.');
            }
        }
        foreach ($renamedTransitions as [$t, $old]) {
            $usedBy = count(array_filter($triggers, fn ($x) => $x->type === TriggerTypes::TRANSITION_APPLIED && in_array($old->key, $x->transitions ?? [], true)))
                + count(array_filter($steps, fn (ApplyTransitionStep $x) => $x->transition === $old->key));
            if ($usedBy > 0) {
                $found[] = new LintProblem('renamedInUse', 'warning', $t->id, "The transition '{$old->key}' is renamed to '{$t->key}' while {$usedBy} other workflow step(s) or trigger(s) use the old key.");
            }
        }

        return $found;
    }

    /** Record instance statuses that keep a record on its lifecycle. */
    private const array LIVE_RECORD = [InstanceStatus::ACTIVE, InstanceStatus::INCIDENT, InstanceStatus::OUT_OF_SYNC, InstanceStatus::COMPLETED];

    /** @return list<DefinitionSummary> all definitions, by title, with their running instance counts */
    public function listDefinitions(): array
    {
        return $this->unit(function (Uow $u) {
            $list = array_map(fn (DefinitionRecord $d) => $this->summary($u, $d), $u->s->definitions());
            usort($list, fn (DefinitionSummary $a, DefinitionSummary $b) => strcasecmp($a->title, $b->title));

            return $list;
        });
    }

    private function summary(Uow $u, DefinitionRecord $d): DefinitionSummary
    {
        $running = $u->s->countInstances(new InstanceQuery(definitionId: $d->id, kinds: [InstanceKinds::AUTOMATION, InstanceKinds::RECORD], statuses: [
            InstanceStatus::RUNNING, InstanceStatus::WAITING, InstanceStatus::INCIDENT, InstanceStatus::QUEUED, InstanceStatus::ACTIVE, InstanceStatus::OUT_OF_SYNC,
        ]));

        return new DefinitionSummary($d->id, $d->key, $d->kind, $d->title, $d->entity, $d->status, $d->version, $d->hasDraftChanges, Instant::format($d->updatedAt),
            $d->updatedBy, $running);
    }

    /** One definition with its draft, published version, history and draft lint problems. Throws `notFound`. */
    public function getDefinition(string $id): DefinitionDetail
    {
        return $this->unit(function (Uow $u) use ($id) {
            $d = $this->requireDefinition($u, $id);
            $perVersion = $u->s->countInstancesByVersion($id);
            $versions = $u->s->versions($id);
            usort($versions, fn (DefinitionVersionRecord $a, DefinitionVersionRecord $b) => $b->version <=> $a->version);
            $published = $d->version > 0 ? Json::decode($u->s->version($id, $d->version)->json) : null;

            return new DefinitionDetail($this->summary($u, $d), Json::decode($d->draftJson), $published,
                array_map(fn (DefinitionVersionRecord $v) => new DefinitionVersionInfo($v->version, $v->note, Instant::format($v->publishedAt), $v->publishedBy,
                    $perVersion[$v->version] ?? 0), $versions),
                $this->lint($u, $d->draftJson));
        });
    }

    /** The JSON of one published version (immutable). Throws `notFound`. */
    public function getVersion(string $id, int $version): stdClass
    {
        return $this->unit(fn (Uow $u) => Json::decode(($u->s->version($id, $version) ?? throw new WorkflowException('notFound', 'That version does not exist.'))->json));
    }

    private function requireDefinition(Uow $u, string $id): DefinitionRecord
    {
        return $u->s->definition($id) ?? throw new WorkflowException('notFound', 'That workflow does not exist.');
    }

    /** Creates a draft from the builder's JSON; the definition's `id` and `key` must be unique. */
    public function createDefinition(string $json, WorkflowActor $actor): DefinitionSummary
    {
        return $this->unit(function (Uow $u) use ($json, $actor) {
            $definition = self::parseForSave($json);
            if ($u->s->definition($definition->id) !== null) {
                throw new WorkflowException('duplicateId', 'A workflow with this id already exists.');
            }
            if ($u->s->definitionByKey($definition->key) !== null) {
                throw new WorkflowException('duplicateKey', "A workflow with the key '{$definition->key}' already exists.");
            }
            $record = new DefinitionRecord;
            $record->id = $definition->id;
            $record->key = $definition->key;
            $record->kind = $definition->kind;
            $record->title = $definition->title;
            $record->entity = $definition->entity;
            $record->draftJson = self::normalizeJson($json);
            $record->createdAt = $record->updatedAt = $u->now;
            $record->createdBy = $record->updatedBy = $actor->id;
            $u->s->addDefinition($record);
            $u->audit(new AuditEntry('definition', 'created', 'definition', $record->id, $actor, definitionId: $record->id,
                data: (object) ['key' => $record->key, 'title' => $record->title, 'kind' => $record->kind]));

            return $this->summary($u, $record);
        });
    }

    /** Saves the draft; published versions are never changed by this. */
    public function saveDraft(string $id, string $json, WorkflowActor $actor): DefinitionSummary
    {
        return $this->unit(function (Uow $u) use ($id, $json, $actor) {
            $record = $this->requireDefinition($u, $id);
            $definition = self::parseForSave($json);
            if ($definition->id !== $id) {
                throw new WorkflowException('idMismatch', "The definition's id cannot change.");
            }
            if ($definition->kind !== $record->kind && $record->version > 0) {
                throw new WorkflowException('kindLocked', 'A published workflow cannot change between automation and lifecycle.');
            }
            if ($definition->key !== $record->key) {
                if ($record->version > 0) {
                    throw new WorkflowException('keyLocked', "A published workflow's key cannot change; other workflows and the API use it.");
                }
                if ($u->s->definitionByKey($definition->key) !== null) {
                    throw new WorkflowException('duplicateKey', "A workflow with the key '{$definition->key}' already exists.");
                }
            }
            $normalized = self::normalizeJson($json);
            $changed = $normalized !== $record->draftJson;
            $record->draftJson = $normalized;
            $record->key = $definition->key;
            $record->kind = $definition->kind;
            $record->title = $definition->title;
            $record->entity = $definition->entity;
            $record->updatedAt = $u->now;
            $record->updatedBy = $actor->id;
            if ($record->version > 0) {
                $published = $u->s->version($id, $record->version);
                $record->hasDraftChanges = $published === null || self::hash($normalized) !== $published->hash;
            }
            $u->s->updateDefinition($record);
            if ($changed) {
                $u->audit(new AuditEntry('definition', 'draftSaved', 'definition', $id, $actor, definitionId: $id));
            }

            return $this->summary($u, $record);
        });
    }

    private static function parseForSave(string $json): WorkflowDefinition
    {
        try {
            $definition = DefinitionJson::parse($json);
        } catch (JsonException $e) {
            throw new WorkflowException('invalidJson', "The definition is not valid JSON: {$e->getMessage()}");
        }
        if (trim($definition->id) === '' || trim($definition->key) === '' || trim($definition->title) === '') {
            throw new WorkflowException('invalidDefinition', 'A workflow needs an id, a key and a title.');
        }
        if (! in_array($definition->kind, [WorkflowKinds::AUTOMATION, WorkflowKinds::LIFECYCLE], true)) {
            throw new WorkflowException('invalidDefinition', 'A workflow is either an automation or a lifecycle.');
        }
        if (strlen($json) > 2000000) {
            throw new WorkflowException('tooLarge', 'The workflow is too large.');
        }

        return $definition;
    }

    private static function normalizeJson(string $json): string
    {
        return Json::encode(Json::decode($json));
    }

    /** SHA-256 of a definition's canonical JSON. */
    public static function hash(string $json): string
    {
        return hash('sha256', Canonical::encode(Json::decode($json)));
    }

    /** Publishes the draft as a new immutable version; lint errors block publishing (`lintErrors` with the problems as details). */
    public function publish(string $id, ?string $note, WorkflowActor $actor): DefinitionSummary
    {
        $result = $this->unit(fn (Uow $u) => $this->publishInUnit($u, $id, $note, $actor));
        // Record writes right after publishing must already see a new lifecycle's field ownership.
        $this->refreshOwnership();

        return $result;
    }

    private function publishInUnit(Uow $u, string $id, ?string $note, WorkflowActor $actor): DefinitionSummary
    {
        $record = $this->requireDefinition($u, $id);
        $errors = array_values(array_filter($this->lint($u, $record->draftJson), fn (LintProblem $p) => $p->severity === 'error'));
        if ($errors !== []) {
            throw new WorkflowException('lintErrors', 'Fix '.count($errors).' problem(s) before publishing.', $errors);
        }
        $definition = DefinitionJson::parse($record->draftJson);
        $previous = $record->version > 0 ? $u->s->version($id, $record->version)->json : null;
        $version = $record->version + 1;
        $draft = Json::decode($record->draftJson);
        $draft->revision = $version;
        $json = Json::encode($draft);
        $v = new DefinitionVersionRecord;
        $v->definitionId = $id;
        $v->version = $version;
        $v->key = $record->key;
        $v->json = $json;
        $v->hash = self::hash($json);
        $v->note = $note;
        $v->publishedAt = $u->now;
        $v->publishedBy = $actor->id;
        $u->s->addVersion($v);
        $record->draftJson = $json;
        $record->version = $version;
        $record->status = DefinitionStatus::PUBLISHED;
        $record->hasDraftChanges = false;
        $record->updatedAt = $u->now;
        $record->updatedBy = $actor->id;
        $u->s->updateDefinition($record);
        $this->indexTriggers($u, $record, DefinitionJson::parse($json), $version);
        $diff = $previous === null ? null : DefinitionDiff::describe(DefinitionJson::parse($previous), $definition);
        $u->audit(new AuditEntry('definition', 'published', 'definition', $id, $actor, definitionId: $id,
            data: (object) ['version' => $version, 'note' => $note, 'diff' => $diff]));
        $this->invalidateOwnership();

        return $this->summary($u, $record);
    }

    // ── Permissions ───────────────────────────────────────────────────────

    /**
     * Changes who may do nodes of a published workflow and publishes straight away (spec §13 permissions): the new
     * version is the current published one plus only these changes; the draft gets the same changes where it still
     * has the node. A node is a transition id, `reset`, `trigger` (a manual trigger) or an approval/task step id.
     * A lifecycle's record instances move to the new version (`instance.migrated`, reason `permissions`).
     *
     * @param  list<array{0: string, 1: list<mixed>}>  $changes  [node, who]
     */
    public function updatePermissions(string $id, array $changes, ?string $note, WorkflowActor $actor): PermissionsSummary
    {
        if ($changes === []) {
            throw new WorkflowException('invalidInput', 'Say which permissions change.');
        }
        $result = $this->unit(function (Uow $u) use ($id, $changes, $note, $actor) {
            $record = $this->requireDefinition($u, $id);
            if ($record->version === 0 || $record->status === DefinitionStatus::ARCHIVED) {
                throw new WorkflowException('notPublished', "Only a published workflow's permissions can be changed here.");
            }
            $current = $u->s->version($id, $record->version);
            $published = Json::decode($current->json);
            foreach ($changes as [$node, $who]) {
                self::applyPermission($published, $node, $who);
            }
            // Nothing would change: no identical version is published.
            if (self::hash(Json::encode($published)) === $current->hash) {
                return new PermissionsSummary($this->summary($u, $record), false, [], 0);
            }
            $hadDraft = $record->hasDraftChanges;
            $previousVersion = $record->version;
            $draft = Json::decode($record->draftJson);
            $skipped = [];
            foreach ($changes as [$node, $who]) {
                try {
                    self::applyPermission($draft, $node, $who);
                } catch (WorkflowException) {
                    $skipped[] = $node;
                }
            }
            $record->draftJson = Json::encode($published);
            $u->s->updateDefinition($record);
            $summary = $this->publishInUnit($u, $id, $note ?? 'Permissions changed', $actor);
            if ($hadDraft) {
                $record = $this->requireDefinition($u, $id);
                $draft->revision = $record->version;
                $record->draftJson = Json::encode($draft);
                $pub = $u->s->version($id, $record->version);
                $record->hasDraftChanges = $pub === null || self::hash($record->draftJson) !== $pub->hash;
                $u->s->updateDefinition($record);
                $summary = $this->summary($u, $record);
            }
            // Existing records follow the new permissions (revoked people stop acting on them).
            [$moved, $kept] = $record->kind === WorkflowKinds::LIFECYCLE ? $this->moveRecordsToVersion($u, $id, $previousVersion, $summary->version, $actor, 'permissions') : [0, 0];
            $u->audit(new AuditEntry('definition', 'permissionsChanged', 'definition', $id, $actor, definitionId: $id, data: (object) [
                'version' => $summary->version,
                'changes' => array_map(fn ($c) => (object) ['node' => $c[0], 'who' => Json::normalize($c[1])], $changes),
                'draftSkipped' => $skipped,
                'recordsMoved' => $moved,
                'recordsKept' => $kept,
            ]));

            return new PermissionsSummary($summary, true, $skipped, $moved, $kept);
        });
        $this->refreshOwnership();

        return $result;
    }

    /**
     * Moves a lifecycle's record instances to `$version` without touching their state; only those whose version has
     * the very same states and transitions as the new one move, the others are counted as kept.
     *
     * @return array{0: int, 1: int} moved, kept
     */
    private function moveRecordsToVersion(Uow $u, string $definitionId, int $previous, int $version, WorkflowActor $actor, string $reason): array
    {
        $target = $this->compiled($u, $definitionId, $version);
        $sameShape = [$previous => true];
        $moved = 0;
        $kept = 0;
        for ($skip = 0; ; $skip += 500) {
            $page = $u->s->queryInstances(new InstanceQuery(definitionId: $definitionId, kinds: [InstanceKinds::RECORD], statuses: self::LIVE_RECORD, skip: $skip, take: 500));
            foreach ($page as $found) {
                $i = $u->records[$found->id] ?? $found;
                if ($i->version >= $version) {
                    continue;
                }
                $sameShape[$i->version] ??= self::shape($this->compiled($u, $definitionId, $i->version)) === self::shape($target);
                if (! $sameShape[$i->version] || ($i->state !== null && $target->stateById($i->state) === null)) {
                    $kept++;

                    continue;
                }
                $from = $i->version;
                $i->version = $version;
                $i->updatedAt = $u->now;
                $u->s->updateInstance($i);
                $u->audit(new AuditEntry('instance', 'migrated', 'instance', $i->id, $actor, entity: $i->entity, entityId: $i->entityId, instanceId: $i->id,
                    definitionId: $definitionId, before: (object) ['version' => $from], after: (object) ['version' => $version], data: (object) ['reason' => $reason],
                    correlationId: $i->correlationId));
                $moved++;
            }
            if (count($page) < 500) {
                return [$moved, $kept];
            }
        }
    }

    /** A lifecycle's states and transitions (what record instances depend on), for comparing versions. */
    private static function shape(CompiledDefinition $c): string
    {
        return Json::encode([
            array_map(fn (State $s) => [$s->id, $s->key, $s->category], $c->lifecycle()->states),
            array_map(fn (Transition $t) => [$t->id, $t->key, $t->to, $t->from->any ? null : $t->from->states], $c->lifecycle()->transitions),
        ]);
    }

    /** Sets who may do one node of a definition's JSON (see `updatePermissions`). @param  list<mixed>  $who */
    private static function applyPermission(stdClass $definition, string $node, array $who): void
    {
        $copy = fn () => Json::copy(Json::normalize($who));
        if ($node === 'trigger') {
            if (($definition->trigger ?? null) instanceof stdClass && ($definition->trigger->type ?? null) === TriggerTypes::MANUAL) {
                $definition->trigger->who = $copy();

                return;
            }
            throw new WorkflowException('unknownNode', 'This workflow has no manual start whose people could change.');
        }
        if ($node === 'reset') {
            if (($definition->lifecycle->reset ?? null) instanceof stdClass) {
                $definition->lifecycle->reset->who = $copy();

                return;
            }
            throw new WorkflowException('unknownNode', 'This workflow cannot be reset.');
        }
        foreach (is_array($definition->lifecycle->transitions ?? null) ? $definition->lifecycle->transitions : [] as $t) {
            if ($t instanceof stdClass && ($t->id ?? null) === $node) {
                $t->who = $copy();

                return;
            }
        }
        $step = array_find(self::allStepObjects($definition), fn (stdClass $s) => ($s->id ?? null) === $node);
        if ($step !== null) {
            if (! in_array($step->type ?? null, ['approval', 'task'], true)) {
                throw new WorkflowException('unknownNode', "Step '{$node}' is not an approval or a task.");
            }
            $step->assignees = $copy();

            return;
        }
        throw new WorkflowException('unknownNode', "'{$node}' is not a transition, the reset, the manual start or a step of this workflow.");
    }

    /**
     * Every step object of a definition's JSON, walking only the places steps live (spec §6, §7), so a form field with
     * the same id is never taken for a step.
     *
     * @return list<stdClass>
     */
    private static function allStepObjects(stdClass $definition): array
    {
        $list = fn ($v) => is_array($v) ? $v : [];
        $nested = function (stdClass $s) use ($list): array {
            $lists = [$list($s->steps ?? null), $list($s->onError->steps ?? null)];
            foreach ($list($s->paths ?? null) as $p) {
                $lists[] = $list($p->steps ?? null);
            }
            foreach ($list($s->cases ?? null) as $c) {
                $lists[] = $list($c->steps ?? null);
            }
            $lists[] = $list($s->otherwise->steps ?? null);
            $outcomes = $s->outcomes ?? null;
            if ($outcomes instanceof stdClass) {
                array_push($lists, $list($outcomes->approved ?? null), $list($outcomes->rejected ?? null), $list($outcomes->timedOut ?? null));
            } elseif (is_array($outcomes)) {
                foreach ($outcomes as $o) {
                    $lists[] = $list($o->steps ?? null);
                }
            }
            $lists[] = $list($s->onTimeout->steps ?? null);
            $lists[] = $list($s->until->onTimeout->steps ?? null);

            return $lists;
        };
        $walk = function (array $steps) use (&$walk, $nested): array {
            $out = [];
            foreach ($steps as $s) {
                if (! $s instanceof stdClass) {
                    continue;
                }
                $out[] = $s;
                foreach ($nested($s) as $inner) {
                    array_push($out, ...$walk($inner));
                }
            }

            return $out;
        };
        $roots = [$list($definition->steps ?? null)];
        $lc = $definition->lifecycle ?? null;
        if ($lc instanceof stdClass) {
            foreach ($list($lc->states ?? null) as $st) {
                array_push($roots, $list($st->onEnter ?? null), $list($st->onExit ?? null), $list($st->sla->steps ?? null));
            }
            foreach ($list($lc->transitions ?? null) as $t) {
                $roots[] = $list($t->steps ?? null);
            }
            $roots[] = $list($lc->reset->steps ?? null);
        }

        return array_merge([], ...array_map($walk, $roots));
    }

    /** @return list<TriggerRecord> the trigger index rows of a version: one per lifecycle (its owned field) or automation trigger */
    public static function triggerRows(WorkflowDefinition $definition, string $definitionId, string $definitionKey, int $version, int $publishedAt): array
    {
        $row = new TriggerRecord;
        $row->id = self::newId();
        $row->definitionId = $definitionId;
        $row->definitionKey = $definitionKey;
        $row->version = $version;
        $row->entity = $definition->entity;
        $row->publishedAt = $publishedAt;
        if ($definition->isLifecycle() && $definition->lifecycle !== null) {
            $lc = $definition->lifecycle;
            $row->type = 'lifecycle';
            $row->field = $lc->field;
            $row->data = Json::encode((object) [
                'mode' => $lc->mode ?? 'enforce', 'initial' => array_find($lc->states, fn (State $s) => $s->isInitial())?->key,
                'appliesWhen' => $lc->appliesWhen?->toJson(),
            ]);

            return [$row];
        }
        $trigger = $definition->trigger;
        if ($trigger === null) {
            return [];
        }
        $row->type = $trigger->type;
        $row->field = $trigger->field;
        $row->lifecycle = $trigger->lifecycle;
        $row->signal = $trigger->signal;
        $row->data = $trigger->cron === null ? null : Json::encode((object) ['cron' => $trigger->cron]);

        return [$row];
    }

    /** Builds the trigger index rows of a published version, and timers for schedules and date triggers. */
    private function indexTriggers(Uow $u, DefinitionRecord $record, WorkflowDefinition $definition, int $version): void
    {
        $trigger = $definition->trigger;
        // Changes are compared with the first publish, so events still in the outbox when a new version is published
        // belong to the workflow (spec §5); restoring version 1 keeps its first publish time too (publish order, spec §6).
        $firstPublished = $u->s->version($record->id, 1)->publishedAt ?? $u->now;
        $u->s->replaceTriggers($record->id, self::triggerRows($definition, $record->id, $record->key, $version, $firstPublished));
        foreach ($u->s->definitionBookmarks($record->id, BookmarkKinds::SCHEDULE) as $b) {
            $u->s->removeBookmark($b->id);
        }
        if ($trigger?->type === TriggerTypes::SCHEDULE && $trigger->cron !== null) {
            [$cron] = Cron::tryParse($trigger->cron);
            if ($cron !== null) {
                $this->planNextSchedule($u, $record->id, $cron);
            }
        }
        if ($trigger?->type === TriggerTypes::DATE_REACHED) {
            $this->planDateTriggersForAll($u, new CompiledDefinition($definition, $record->id, $version));
        } else {
            foreach ($u->s->definitionBookmarks($record->id, BookmarkKinds::DATE_REACHED) as $b) {
                $u->s->removeBookmark($b->id);
            }
        }
    }

    /** Archives a definition: it starts nothing new; running instances finish on their version. Audited as `definition.archived`. */
    public function archive(string $id, WorkflowActor $actor): DefinitionSummary
    {
        return $this->setStatus($id, DefinitionStatus::ARCHIVED, 'archived', $actor);
    }

    /** Restores an archived definition (linted like a publish first: `lintErrors`) and re-indexes its triggers. Audited as `definition.restored`. */
    public function restore(string $id, WorkflowActor $actor): DefinitionSummary
    {
        return $this->setStatus($id, null, 'restored', $actor);
    }

    private function setStatus(string $id, ?string $status, string $action, WorkflowActor $actor): DefinitionSummary
    {
        $result = $this->unit(function (Uow $u) use ($id, $status, $action, $actor) {
            $record = $this->requireDefinition($u, $id);
            if ($status === null && $record->version > 0 && $record->status === DefinitionStatus::ARCHIVED) {
                // Coming back must not clash with what was published meanwhile: the version is linted like a publish.
                $errors = array_values(array_filter($this->lint($u, $u->s->version($id, $record->version)->json), fn (LintProblem $p) => $p->severity === 'error'));
                if ($errors !== []) {
                    throw new WorkflowException('lintErrors', 'Fix '.count($errors).' problem(s) before restoring.', $errors);
                }
            }
            $wasArchived = $record->status === DefinitionStatus::ARCHIVED;
            $record->status = $status ?? ($record->version > 0 ? DefinitionStatus::PUBLISHED : DefinitionStatus::DRAFT);
            $record->updatedAt = $u->now;
            $record->updatedBy = $actor->id;
            $u->s->updateDefinition($record);
            if ($status === DefinitionStatus::ARCHIVED) {
                $u->s->replaceTriggers($id, []);
                foreach ([BookmarkKinds::SCHEDULE, BookmarkKinds::DATE_REACHED] as $kind) {
                    foreach ($u->s->definitionBookmarks($id, $kind) as $b) {
                        $u->s->removeBookmark($b->id);
                    }
                }
                // An archived lifecycle moves nothing: its records' automatic transitions and SLAs stop too.
                if ($record->kind === WorkflowKinds::LIFECYCLE) {
                    $this->archiveRecordTimers($u, $id, false);
                }
            } elseif ($record->version > 0) {
                $this->indexTriggers($u, $record, DefinitionJson::parse($u->s->version($id, $record->version)->json), $record->version);
                if ($wasArchived && $record->kind === WorkflowKinds::LIFECYCLE) {
                    $this->archiveRecordTimers($u, $id, true);
                }
            }
            $u->audit(new AuditEntry('definition', $action, 'definition', $id, $actor, definitionId: $id));
            $this->invalidateOwnership();

            return $this->summary($u, $record);
        });
        $this->refreshOwnership();

        return $result;
    }

    /** Publishes a copy of an earlier version as the newest version, in one unit of work. */
    public function rollback(string $id, int $version, WorkflowActor $actor): DefinitionSummary
    {
        $result = $this->unit(function (Uow $u) use ($id, $version, $actor) {
            $record = $this->requireDefinition($u, $id);
            $v = $u->s->version($id, $version) ?? throw new WorkflowException('notFound', 'That version does not exist.');
            $record->draftJson = $v->json;
            $record->hasDraftChanges = true;
            $old = DefinitionJson::parse($v->json);
            $record->title = $old->title;
            $record->entity = $old->entity;
            $record->updatedAt = $u->now;
            $record->updatedBy = $actor->id;
            $u->s->updateDefinition($record);
            $u->audit(new AuditEntry('definition', 'rolledBack', 'definition', $id, $actor, definitionId: $id, data: (object) ['toVersion' => $version]));

            return $this->publishInUnit($u, $id, "Rolled back to version {$version}", $actor);
        });
        $this->refreshOwnership();

        return $result;
    }

    /** Deletes a workflow that was never published. */
    public function deleteDefinition(string $id, WorkflowActor $actor): void
    {
        $this->unit(function (Uow $u) use ($id, $actor) {
            $record = $this->requireDefinition($u, $id);
            if ($record->version > 0) {
                throw new WorkflowException('published', 'A published workflow cannot be deleted; archive it instead.');
            }
            $u->s->deleteDefinition($id);
            $u->audit(new AuditEntry('definition', 'deleted', 'definition', $id, $actor, definitionId: $id, data: (object) ['key' => $record->key]));
        });
    }

    /** @return list<string> plain-English changes between the published version and the draft */
    public function diffDraft(string $id): array
    {
        return $this->unit(function (Uow $u) use ($id) {
            $record = $this->requireDefinition($u, $id);
            if ($record->version === 0) {
                return ['Not published yet.'];
            }

            return DefinitionDiff::describe(DefinitionJson::parse($u->s->version($id, $record->version)->json), DefinitionJson::parse($record->draftJson));
        });
    }
}
