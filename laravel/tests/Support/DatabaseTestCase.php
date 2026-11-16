<?php

namespace Zouriel\Workflows\Tests\Support;

use Illuminate\Database\ConnectionInterface;
use Illuminate\Support\Facades\DB;
use Orchestra\Testbench\TestCase;
use Zouriel\Workflows\Laravel\WorkflowsServiceProvider;

/**
 * A Laravel app with the workflow tables migrated on the test database (`TestDatabase`). PostgreSQL keeps its tables
 * between tests and empties them before each one; SQLite in memory starts empty every time.
 */
abstract class DatabaseTestCase extends TestCase
{
    protected function getPackageProviders($app): array
    {
        return [WorkflowsServiceProvider::class];
    }

    protected function defineEnvironment($app): void
    {
        $app['config']->set('database.default', 'testing');
        $app['config']->set('database.connections.testing', TestDatabase::config());
        // A second connection to the same database, for tests of two workers or two requests at once.
        $app['config']->set('database.connections.other', TestDatabase::config());
    }

    protected function setUp(): void
    {
        parent::setUp();
        self::prepare(DB::connection());
    }

    /** Empties the workflow tables, creating them first when the database has none. */
    public static function prepare(ConnectionInterface $db): void
    {
        $schema = $db->getSchemaBuilder();
        if (! $schema->hasTable('wf_definitions')) {
            (require __DIR__.'/../../database/migrations/2026_11_16_000000_create_workflow_tables.php')->up();
            DatabaseHostStore::createRecordsTable($db);

            return;
        }
        foreach ($schema->getTableListing() as $table) {
            $name = str_contains($table, '.') ? substr($table, strrpos($table, '.') + 1) : $table;
            if (str_starts_with($name, 'wf_') || str_starts_with($name, 'test_')) {
                $db->table($name)->delete();
            }
        }
    }
}
