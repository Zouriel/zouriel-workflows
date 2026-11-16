<?php

use Illuminate\Database\Migrations\Migration;
use Illuminate\Database\Schema\Blueprint;
use Illuminate\Support\Facades\Schema;

/**
 * The engine's twelve tables, as the .NET EF model maps them (`WorkflowModel.cs`): same names (with the configured
 * prefix), snake_case columns, keys and indexes. Two deliberate differences: instants are milliseconds since 1970 UTC
 * in `bigInteger` columns (exact on every database, nothing to truncate before hashing), and tables without a natural
 * order get an `ord` column so rows written in the same millisecond keep the order they were written in. JSON is
 * stored as text, never `json`/`jsonb`: audit hashes and definition round-trips need the bytes the engine wrote.
 */
return new class extends Migration
{
    public function getConnection(): ?string
    {
        return config('workflows.connection');
    }

    private function table(string $name): string
    {
        return config('workflows.table_prefix', 'wf_').$name;
    }

    public function up(): void
    {
        Schema::create($this->table('definitions'), function (Blueprint $t) {
            $t->string('id', 64)->primary();
            $t->string('key', 64)->unique();
            $t->string('kind', 32);
            $t->text('title');
            $t->string('entity', 128)->nullable();
            $t->string('status', 32);
            $t->integer('version');
            $t->longText('draft_json');
            $t->boolean('has_draft_changes');
            $t->bigInteger('created_at');
            $t->string('created_by', 191)->nullable();
            $t->bigInteger('updated_at');
            $t->string('updated_by', 191)->nullable();
            $t->bigInteger('row_version');
            $t->bigInteger('ord');
        });
        Schema::create($this->table('definition_versions'), function (Blueprint $t) {
            $t->string('definition_id', 64);
            $t->integer('version');
            $t->string('key', 64);
            $t->longText('json');
            $t->string('hash', 128);
            $t->text('note')->nullable();
            $t->bigInteger('published_at');
            $t->string('published_by', 191)->nullable();
            $t->primary(['definition_id', 'version']);
        });
        Schema::create($this->table('triggers'), function (Blueprint $t) {
            $t->string('id', 64)->primary();
            $t->string('definition_id', 64)->index();
            $t->string('definition_key', 64);
            $t->integer('version');
            $t->string('type', 32);
            $t->string('entity', 128)->nullable();
            $t->string('field', 128)->nullable();
            $t->string('lifecycle', 128)->nullable();
            $t->string('signal', 128)->nullable();
            $t->bigInteger('published_at');
            $t->text('data')->nullable();
            $t->bigInteger('ord');
            $t->index(['entity', 'type']);
        });
        Schema::create($this->table('trigger_claims'), function (Blueprint $t) {
            $t->string('definition_id', 64);
            $t->string('event_id', 64);
            $t->bigInteger('claimed_at')->index();
            $t->primary(['definition_id', 'event_id']);
        });
        Schema::create($this->table('instances'), function (Blueprint $t) {
            $t->string('id', 64)->primary();
            $t->string('definition_id', 64);
            $t->string('definition_key', 64);
            $t->integer('version');
            $t->string('kind', 32);
            $t->string('parent_id', 64)->nullable()->index();
            $t->text('reason')->nullable();
            $t->string('entity', 128)->nullable();
            $t->string('entity_id', 191)->nullable();
            $t->string('status', 32);
            $t->string('state', 128)->nullable();
            $t->bigInteger('state_entered_at')->nullable();
            $t->longText('data');
            $t->longText('trigger_json')->nullable();
            $t->integer('depth');
            $t->string('initiator_id', 191)->nullable();
            $t->text('initiator_name')->nullable();
            $t->text('error')->nullable();
            $t->string('error_step_id', 128)->nullable();
            $t->string('correlation_id', 64);
            $t->bigInteger('started_at');
            $t->bigInteger('updated_at');
            $t->bigInteger('ended_at')->nullable();
            $t->bigInteger('row_version');
            // Unique while set: of two units starting the same live instance, the database refuses the second, which
            // is retried. NULLs never collide in a unique index on PostgreSQL, MySQL or SQLite.
            $t->string('live_key', 255)->nullable()->unique();
            $t->bigInteger('ord');
            $t->index(['entity', 'entity_id']);
            $t->index(['definition_id', 'status']);
        });
        Schema::create($this->table('steps'), function (Blueprint $t) {
            $t->string('id', 64)->primary();
            $t->string('instance_id', 64)->index();
            $t->string('node_id', 128);
            $t->string('step_type', 32);
            $t->string('token_id', 64)->nullable();
            $t->integer('attempt');
            $t->string('status', 32);
            $t->longText('input')->nullable();
            $t->longText('output')->nullable();
            $t->text('error')->nullable();
            $t->longText('undo')->nullable();
            $t->bigInteger('started_at');
            $t->bigInteger('ended_at')->nullable();
            $t->bigInteger('ord');
        });
        Schema::create($this->table('bookmarks'), function (Blueprint $t) {
            $t->string('id', 64)->primary();
            $t->string('instance_id', 64)->nullable()->index();
            $t->string('token_id', 64)->nullable();
            $t->string('node_id', 128)->nullable();
            $t->string('kind', 32);
            $t->string('purpose', 32);
            $t->bigInteger('due_at')->nullable()->index();
            $t->string('signal', 128)->nullable();
            $t->string('entity', 128)->nullable();
            $t->string('entity_id', 191)->nullable();
            $t->string('definition_id', 64)->nullable();
            $t->string('task_id', 64)->nullable();
            $t->longText('payload')->nullable();
            $t->bigInteger('created_at');
            $t->bigInteger('locked_until')->nullable();
            $t->integer('attempts');
            $t->text('error')->nullable();
            $t->bigInteger('ord');
            $t->index(['kind', 'entity', 'entity_id']);
        });
        Schema::create($this->table('tasks'), function (Blueprint $t) {
            $t->string('id', 64)->primary();
            $t->string('instance_id', 64)->index();
            $t->string('root_instance_id', 64)->nullable();
            $t->string('node_id', 128);
            $t->string('token_id', 64);
            $t->string('kind', 32);
            $t->text('title');
            $t->longText('instructions')->nullable();
            $t->string('definition_key', 64);
            $t->string('entity', 128)->nullable();
            $t->string('entity_id', 191)->nullable();
            $t->text('entity_title')->nullable();
            $t->string('status', 32);
            $t->longText('assignees');
            $t->text('rule')->nullable();
            $t->longText('outcomes');
            $t->longText('decisions');
            $t->longText('form')->nullable();
            $t->string('require_comment', 32)->nullable();
            $t->boolean('allow_delegation');
            $t->string('outcome', 128)->nullable();
            $t->bigInteger('due_at');
            $t->integer('escalation_level');
            $t->bigInteger('created_at');
            $t->bigInteger('completed_at')->nullable();
            $t->bigInteger('row_version');
            $t->bigInteger('ord');
            $t->index(['status', 'due_at']);
            $t->index(['entity', 'entity_id']);
        });
        Schema::create($this->table('outbox'), function (Blueprint $t) {
            $t->string('id', 64)->primary();
            $t->string('kind', 32);
            $t->longText('payload');
            $t->bigInteger('created_at');
            $t->bigInteger('processed_at')->nullable();
            $t->integer('attempts');
            $t->bigInteger('locked_until')->nullable();
            $t->text('error')->nullable();
            $t->string('record_key', 255)->nullable();
            $t->index(['processed_at', 'created_at']);
            // One record's pending rows, oldest first (hasEarlierPendingOutbox).
            $t->index(['record_key', 'processed_at', 'created_at']);
        });
        Schema::create($this->table('audit'), function (Blueprint $t) {
            $t->string('id', 64)->primary();
            // The chain's tenant, never null ("" without tenancy): NULLs would not collide in the unique index below.
            $t->string('tenant_key', 64);
            $t->bigInteger('seq');
            // `at` is the exact text the hash covers; `at_ms` the same instant, for range filters.
            $t->string('at', 40);
            $t->bigInteger('at_ms');
            $t->string('actor_type', 32);
            $t->string('actor_id', 191)->nullable();
            $t->text('actor_name')->nullable();
            $t->string('category', 32);
            $t->string('action', 64);
            $t->string('subject_type', 32);
            $t->string('subject_id', 191);
            $t->string('entity', 128)->nullable();
            $t->string('entity_id', 191)->nullable();
            $t->string('instance_id', 64)->nullable()->index();
            $t->string('definition_id', 64)->nullable();
            $t->string('correlation_id', 64);
            $t->string('causation_id', 64)->nullable();
            $t->longText('before')->nullable();
            $t->longText('after')->nullable();
            $t->longText('data')->nullable();
            $t->string('prev_hash', 128);
            $t->string('hash', 128);
            $t->unique(['tenant_key', 'seq']);
            $t->index(['category', 'seq']);
            $t->index(['entity', 'entity_id']);
            $t->index(['category', 'at_ms']);
        });
        Schema::create($this->table('audit_head'), function (Blueprint $t) {
            $t->string('tenant_key', 64)->primary();
            $t->bigInteger('seq');
            $t->string('hash', 128);
        });
        Schema::create($this->table('settings'), function (Blueprint $t) {
            $t->string('key', 32)->primary();
            $t->longText('json');
            $t->bigInteger('revision');
            $t->bigInteger('updated_at');
            $t->string('updated_by', 191)->nullable();
        });
    }

    public function down(): void
    {
        foreach (['settings', 'audit_head', 'audit', 'outbox', 'tasks', 'bookmarks', 'steps', 'instances', 'trigger_claims', 'triggers', 'definition_versions', 'definitions'] as $table) {
            Schema::dropIfExists($this->table($table));
        }
    }
};
