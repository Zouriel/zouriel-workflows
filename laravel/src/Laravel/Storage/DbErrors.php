<?php

namespace Zouriel\Workflows\Laravel\Storage;

use Closure;
use Illuminate\Database\QueryException;
use PDOException;
use Throwable;
use Zouriel\Workflows\Core\Storage\WorkflowConcurrencyException;

/**
 * Recognises database errors that mean "another unit got there first", from the drivers' error codes (never message
 * text), so the engine retries the unit. Port of .NET `DbErrors.cs`.
 */
final class DbErrors
{
    /**
     * Runs a database call on the workflow tables, turning races into `WorkflowConcurrencyException`.
     *
     * @template T
     *
     * @param  Closure(): T  $work
     * @return T
     */
    public static function map(Closure $work): mixed
    {
        try {
            return $work();
        } catch (QueryException|PDOException $e) {
            $mapped = self::toConcurrency($e);
            if ($mapped !== null) {
                throw $mapped;
            }
            throw $e;
        }
    }

    /** The concurrency exception an error on a workflow table stands for, or null when it is a real failure. */
    public static function toConcurrency(Throwable $e): ?WorkflowConcurrencyException
    {
        return match (true) {
            self::isUniqueViolation($e) => new WorkflowConcurrencyException("Another unit wrote the same workflow row: {$e->getMessage()}", 0, $e),
            self::isDeadlock($e) => new WorkflowConcurrencyException("The database chose this unit as a deadlock victim: {$e->getMessage()}", 0, $e),
            self::isBusy($e) => new WorkflowConcurrencyException("The database was locked by another unit: {$e->getMessage()}", 0, $e),
            default => null,
        };
    }

    /** A unique or primary key violation (PostgreSQL 23505, MySQL 1062, SQLite 19 with a UNIQUE/PRIMARY KEY extended code). */
    public static function isUniqueViolation(Throwable $e): bool
    {
        [$state, $code] = self::codes($e);

        return $state === '23505' || ($state === '23000' && in_array($code, [1062, 1586], true))
            || ($code === 19 && preg_match('/UNIQUE|PRIMARY KEY/', $e->getMessage()) === 1);
    }

    /** A deadlock or serialization failure (PostgreSQL 40P01/40001, MySQL 1213/1205). */
    public static function isDeadlock(Throwable $e): bool
    {
        [$state, $code] = self::codes($e);

        return in_array($state, ['40P01', '40001'], true) || in_array($code, [1213, 1205], true);
    }

    /** SQLite could not take a lock (SQLITE_BUSY 5, SQLITE_LOCKED 6). */
    public static function isBusy(Throwable $e): bool
    {
        [$state, $code] = self::codes($e);

        return $state === 'HY000' && in_array($code, [5, 6], true);
    }

    /** @return array{0: ?string, 1: ?int} SQLSTATE and the driver's own code */
    private static function codes(Throwable $e): array
    {
        for ($x = $e; $x !== null; $x = $x->getPrevious()) {
            if ($x instanceof PDOException && is_array($x->errorInfo)) {
                return [$x->errorInfo[0] ?? null, isset($x->errorInfo[1]) ? (int) $x->errorInfo[1] : null];
            }
        }

        return [null, null];
    }
}
