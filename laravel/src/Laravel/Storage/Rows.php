<?php

namespace Zouriel\Workflows\Laravel\Storage;

use ReflectionClass;
use ReflectionNamedType;
use ReflectionProperty;
use stdClass;
use Zouriel\Workflows\Core\Audit\AuditRecord;
use Zouriel\Workflows\Core\Time\Instant;

/**
 * Maps the engine's record classes (`Core\Storage\*Record`, `AuditRecord`) to table rows and back: each public property
 * is the snake_case column of its name (as the EF model maps them), typed by the property on the way back, because
 * drivers return integers and booleans in different shapes. The audit chain's tenant is stored as `tenant_key` (never
 * null) and its instant also as `at_ms`, for range filters.
 */
final class Rows
{
    /** @var array<class-string, array<string, array{0: ReflectionProperty, 1: string, 2: string, 3: bool}>> */
    private static array $columns = [];

    /** @return array<string, mixed> the row for `$record` */
    public static function toRow(object $record): array
    {
        $row = [];
        foreach (self::columns($record::class) as $column => [$property]) {
            $row[$column] = $property->getValue($record);
        }
        if ($record instanceof AuditRecord) {
            unset($row['tenant']);
            $row['tenant_key'] = $record->tenant ?? '';
            $row['at_ms'] = Instant::parse($record->at) ?? 0;
        }

        return $row;
    }

    /**
     * @template T of object
     *
     * @param  class-string<T>  $class
     * @return T
     */
    public static function fromRow(string $class, stdClass $row): object
    {
        $record = new ReflectionClass($class)->newInstanceWithoutConstructor();
        foreach (self::columns($class) as $column => [$property, $type, , $nullable]) {
            $value = $class === AuditRecord::class && $column === 'tenant'
                ? (($row->tenant_key ?? '') === '' ? null : $row->tenant_key)
                : ($row->{$column} ?? null);
            $property->setValue($record, $value === null && $nullable ? null : match ($type) {
                'int' => (int) $value,
                'bool' => is_string($value) ? in_array(strtolower($value), ['1', 't', 'true'], true) : (bool) $value,
                default => (string) $value,
            });
        }

        return $record;
    }

    /** @return array<string, array{0: ReflectionProperty, 1: string, 2: string, 3: bool}> column => property, type, name, nullable */
    private static function columns(string $class): array
    {
        if (isset(self::$columns[$class])) {
            return self::$columns[$class];
        }
        $columns = [];
        foreach (new ReflectionClass($class)->getProperties(ReflectionProperty::IS_PUBLIC) as $property) {
            if ($property->isStatic()) {
                continue;
            }
            $type = $property->getType();
            $columns[self::snake($property->getName())] = [$property, $type instanceof ReflectionNamedType ? $type->getName() : 'string',
                $property->getName(), $type === null || $type->allowsNull()];
        }

        return self::$columns[$class] = $columns;
    }

    public static function snake(string $name): string
    {
        return strtolower(preg_replace('/(?<!^)[A-Z]/', '_$0', $name));
    }
}
