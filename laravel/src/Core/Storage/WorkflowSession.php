<?php

namespace Zouriel\Workflows\Core\Storage;

use Zouriel\Workflows\Core\Audit\AuditRecord;

/**
 * One unit of work. The engine writes workflow state, audit and outbox rows through it; with a database
 * store the host's record changes made through `entities()` commit in the same transaction. Port of
 * .NET `Storage/Store.cs` `IWorkflowSession`.
 */
interface WorkflowSession
{
    public function entities(): EntityGateway;

    public function definition(string $id): ?DefinitionRecord;

    public function definitionByKey(string $key): ?DefinitionRecord;

    /** @return list<DefinitionRecord> */
    public function definitions(): array;

    public function addDefinition(DefinitionRecord $d): void;

    public function updateDefinition(DefinitionRecord $d): void;

    public function deleteDefinition(string $id): void;

    public function version(string $definitionId, int $version): ?DefinitionVersionRecord;

    /** @return list<DefinitionVersionRecord> oldest first */
    public function versions(string $definitionId): array;

    public function addVersion(DefinitionVersionRecord $v): void;

    /** @return list<TriggerRecord> every live trigger index row */
    public function triggers(): array;

    /** Replaces a definition's trigger rows (publish, archive, restore). @param  list<TriggerRecord>  $triggers */
    public function replaceTriggers(string $definitionId, array $triggers): void;

    /** Records that a change event started (or was considered by) a trigger; false when it already did. */
    public function claimTriggerEvent(string $definitionId, string $eventId): bool;

    public function instance(string $id): ?InstanceRecord;

    /** @return list<InstanceRecord> newest first */
    public function queryInstances(InstanceQuery $q): array;

    public function countInstances(InstanceQuery $q): int;

    /** @return array<int, int> instances (automation and record kinds) per version */
    public function countInstancesByVersion(string $definitionId): array;

    public function addInstance(InstanceRecord $i): void;

    /** Saves an instance; refused at commit when another unit changed it since it was read. */
    public function updateInstance(InstanceRecord $i): void;

    public function addStep(StepRecord $s): void;

    public function updateStep(StepRecord $s): void;

    /** @return list<StepRecord> oldest first */
    public function steps(string $instanceId): array;

    public function addBookmark(BookmarkRecord $b): void;

    /** No error when it is already gone. */
    public function removeBookmark(string $id): void;

    public function updateBookmark(BookmarkRecord $b): void;

    public function bookmark(string $id): ?BookmarkRecord;

    /** @return list<BookmarkRecord> */
    public function bookmarks(string $instanceId): array;

    /** @return list<BookmarkRecord> due timers, claimed until `$lockUntil` so other workers skip them */
    public function claimDueBookmarks(int $now, int $lockUntil, int $limit): array;

    /**
     * Bookmarks of a kind about one record; `$entity`/`$entityId` match exactly (null matches bookmarks
     * about no record); `$signal` null matches any signal.
     *
     * @return list<BookmarkRecord>
     */
    public function findBookmarks(string $kind, ?string $entity, ?string $entityId, ?string $signal): array;

    /** @return list<BookmarkRecord> a definition's trigger bookmarks of a kind (not instance ones) */
    public function definitionBookmarks(string $definitionId, string $kind): array;

    public function task(string $id): ?TaskRecord;

    /** @return list<TaskRecord> open first, then by due time */
    public function queryTasks(TaskQuery $q): array;

    public function countTasks(TaskQuery $q): int;

    public function addTask(TaskRecord $t): void;

    public function updateTask(TaskRecord $t): void;

    public function addOutbox(OutboxRecord $o): void;

    /** @return list<OutboxRecord> pending rows, oldest first, claimed until `$lockUntil` */
    public function claimOutbox(int $now, int $lockUntil, int $limit): array;

    public function updateOutbox(OutboxRecord $o): void;

    /** Whether an unprocessed row of the same record was written before `$row`, so one record's events go in order. */
    public function hasEarlierPendingOutbox(OutboxRecord $row): bool;

    /** The head of a tenant's audit chain (null before the first entry), locked for the rest of the unit. */
    public function auditHead(?string $tenant): ?AuditHead;

    public function setAuditHead(?string $tenant, AuditHead $head): void;

    public function addAudit(AuditRecord $a): void;

    /** @return list<AuditRecord> newest first, or oldest first with `fromSeq` */
    public function queryAudit(AuditQuery $q): array;

    /** Deletes a category's entries older than `$before` in one tenant's chain and reports exactly which went. */
    public function purgeAudit(?string $tenant, string $category, int $before): AuditPurgeResult;

    /** Deletes processed outbox rows and trigger claims older than the given instants. */
    public function cleanup(int $processedOutboxBefore, int $claimsBefore): int;

    /** Starts a savepoint (around app actions); false when the store cannot. */
    public function createSavepoint(string $name): bool;

    public function rollbackToSavepoint(string $name): void;

    public function releaseSavepoint(string $name): void;

    public function settings(): ?SettingsRecord;

    public function saveSettings(SettingsRecord $s): void;

    /** Commits everything written in this unit, or throws `WorkflowConcurrencyException`. */
    public function commit(): void;
}
