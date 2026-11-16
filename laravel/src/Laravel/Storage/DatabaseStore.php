<?php

namespace Zouriel\Workflows\Laravel\Storage;

use Closure;
use Illuminate\Database\ConnectionInterface;
use Illuminate\Database\Query\Builder;
use Zouriel\Workflows\Core\Audit\AuditRecord;
use Zouriel\Workflows\Core\Storage\BookmarkRecord;
use Zouriel\Workflows\Core\Storage\DefinitionRecord;
use Zouriel\Workflows\Core\Storage\DefinitionVersionRecord;
use Zouriel\Workflows\Core\Storage\EntityGateway;
use Zouriel\Workflows\Core\Storage\InstanceRecord;
use Zouriel\Workflows\Core\Storage\OutboxRecord;
use Zouriel\Workflows\Core\Storage\SettingsRecord;
use Zouriel\Workflows\Core\Storage\StepRecord;
use Zouriel\Workflows\Core\Storage\TaskRecord;
use Zouriel\Workflows\Core\Storage\TriggerClaim;
use Zouriel\Workflows\Core\Storage\TriggerRecord;
use Zouriel\Workflows\Core\Storage\WorkflowStore;
use Zouriel\Workflows\Core\Time\Clock;

/**
 * Keeps workflow state in the app's database (the `wf_*` tables of the package migration), on the connection the
 * app's models use, so a unit of work's workflow rows, audit, outbox and the record writes it makes commit together.
 * Port of .NET `EfWorkflowStore`.
 *
 * A session starts its transaction at its first write (or locking read), so units that only read (views, the change
 * hook loading owned fields) never hold locks. Instances and tasks are checked with their `row_version`
 * (`UPDATE … WHERE row_version = ?`), live keys and trigger claims with unique keys, and the audit chain head with a
 * row lock, so a unit that lost a race fails with `WorkflowConcurrencyException` and the engine retries it, as the
 * in-memory store does at commit. Timers and outbox rows are claimed with `FOR UPDATE SKIP LOCKED` on PostgreSQL and
 * MySQL, so several workers never take the same row; SQLite serialises writers itself.
 */
final class DatabaseStore implements WorkflowStore
{
    /** The logical tables (as `InMemoryStore` names them) with their record class and table name. */
    public const array TABLES = [
        'definitions' => [DefinitionRecord::class, 'definitions'],
        'versions' => [DefinitionVersionRecord::class, 'definition_versions'],
        'triggers' => [TriggerRecord::class, 'triggers'],
        'claims' => [TriggerClaim::class, 'trigger_claims'],
        'instances' => [InstanceRecord::class, 'instances'],
        'steps' => [StepRecord::class, 'steps'],
        'bookmarks' => [BookmarkRecord::class, 'bookmarks'],
        'tasks' => [TaskRecord::class, 'tasks'],
        'outbox' => [OutboxRecord::class, 'outbox'],
        'audit' => [AuditRecord::class, 'audit'],
        'settings' => [SettingsRecord::class, 'settings'],
    ];

    private static int $counter = 0;

    /**
     * @param  Closure(DatabaseSession): EntityGateway  $gateway  makes the record gateway of each session
     */
    public function __construct(
        public readonly ConnectionInterface $db,
        public readonly Clock $clock,
        private readonly Closure $gateway,
        public readonly string $prefix = 'wf_',
    ) {}

    public function begin(): DatabaseSession
    {
        return new DatabaseSession($this, $this->gateway);
    }

    /** A query on one of the workflow tables (`definitions`, `instances`... as in `TABLES`). */
    public function table(string $table): Builder
    {
        return $this->db->table($this->prefix.(self::TABLES[$table][1] ?? $table));
    }

    /** The full name of a workflow table, quoted for raw SQL. */
    public function wrapTable(string $table): string
    {
        return $this->db->getQueryGrammar()->wrapTable($this->prefix.(self::TABLES[$table][1] ?? $table));
    }

    public function driver(): string
    {
        return $this->db->getDriverName();
    }

    /**
     * Writes a change event's outbox row on the store's connection (the change hook calls this inside the record write's
     * transaction, so the event commits only with the write).
     */
    public function insertOutbox(OutboxRecord $row): void
    {
        DbErrors::map(fn () => $this->table('outbox')->insert(Rows::toRow($row)));
    }

    /** An insertion order: rows written in the same millisecond keep the order they were written in. */
    public static function nextOrder(): int
    {
        [$fraction, $seconds] = explode(' ', microtime());

        return ((int) $seconds * 1000000 + (int) round((float) $fraction * 1000000)) * 1000 + (self::$counter++ % 1000);
    }

    /**
     * Copies of every committed row of a logical table, in insertion order (for tests and tools).
     *
     * @return list<object>
     */
    public function all(string $table): array
    {
        [$class] = self::TABLES[$table];
        $query = $this->table($table);
        $query = match ($table) {
            'audit' => $query->orderBy('tenant_key')->orderBy('seq'),
            'outbox' => $query->orderBy('created_at')->orderBy('id'),
            'versions' => $query->orderBy('definition_id')->orderBy('version'),
            'claims' => $query->orderBy('claimed_at'),
            'settings' => $query,
            default => $query->orderBy('ord'),
        };

        return $query->get()->map(fn ($row) => $table === 'claims'
            ? new TriggerClaim("{$row->definition_id}:{$row->event_id}", (int) $row->claimed_at)
            : Rows::fromRow($class, $row))->all();
    }
}
