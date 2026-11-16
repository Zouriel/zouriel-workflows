<?php

namespace Zouriel\Workflows\Tests\Support;

use PDO;

/**
 * The database the Laravel tests run on: SQLite in memory by default, or PostgreSQL when `DB_CONNECTION=pgsql` is set
 * (host, port, user and password from `DB_HOST`, `DB_PORT`, `DB_USERNAME`, `DB_PASSWORD`; defaults: the local
 * `adducity-postgres` container). On PostgreSQL each test process creates its own `wf_test_<random>` database and drops
 * it when it exits, so tests never touch a database they did not create.
 */
final class TestDatabase
{
    private static ?string $database = null;

    public static function driver(): string
    {
        return getenv('DB_CONNECTION') === 'pgsql' ? 'pgsql' : 'sqlite';
    }

    /** @return array<string, mixed> the connection configuration */
    public static function config(): array
    {
        if (self::driver() === 'sqlite') {
            return ['driver' => 'sqlite', 'database' => ':memory:', 'prefix' => '', 'foreign_key_constraints' => true];
        }

        return self::pgsql(self::database());
    }

    /** @return array<string, mixed> */
    public static function pgsql(string $database): array
    {
        return [
            'driver' => 'pgsql', 'host' => getenv('DB_HOST') ?: '127.0.0.1', 'port' => getenv('DB_PORT') ?: '55440', 'database' => $database,
            'username' => getenv('DB_USERNAME') ?: 'adducity', 'password' => getenv('DB_PASSWORD') ?: 'adducity', 'charset' => 'utf8', 'prefix' => '',
            'search_path' => 'public', 'sslmode' => 'prefer',
        ];
    }

    private static function database(): string
    {
        if (self::$database !== null) {
            return self::$database;
        }
        $name = 'wf_test_'.bin2hex(random_bytes(6));
        $admin = self::admin();
        $admin->exec("CREATE DATABASE {$name}");
        register_shutdown_function(static function () use ($name) {
            self::admin()->exec("DROP DATABASE IF EXISTS {$name} WITH (FORCE)");
        });

        return self::$database = $name;
    }

    private static function admin(): PDO
    {
        $c = self::pgsql('postgres');

        return new PDO("pgsql:host={$c['host']};port={$c['port']};dbname=postgres", $c['username'], $c['password'], [PDO::ATTR_ERRMODE => PDO::ERRMODE_EXCEPTION]);
    }
}
