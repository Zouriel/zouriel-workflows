<?php

namespace Zouriel\Workflows\Laravel\Eloquent;

use BackedEnum;
use DateTimeInterface;
use Illuminate\Contracts\Support\Arrayable;
use Illuminate\Database\Eloquent\Model;
use Illuminate\Database\Eloquent\Relations\BelongsTo;
use Illuminate\Database\Eloquent\Relations\Relation;
use JsonSerializable;
use ReflectionEnum;
use stdClass;
use UnitEnum;
use Zouriel\Forms\Json;
use Zouriel\Workflows\Core\Json\Values;
use Zouriel\Workflows\Core\Registry\EntityDescriptor;
use Zouriel\Workflows\Core\Registry\FieldDescriptor;
use Zouriel\Workflows\Core\Registry\FieldTypes;
use Zouriel\Workflows\Core\Time\Instant;
use Zouriel\Workflows\Core\WorkflowException;

/**
 * Reads a model's registered fields as workflow values and writes workflow values back (spec §2):
 *
 * - enums give their backed value (a pure enum its case name); enum fields accept the value and the model's cast makes the case;
 * - dates give `YYYY-MM-DD` for `date` fields and the instant (`…ss.fffZ`, UTC) for the rest;
 * - numbers are floats (decimal casts arrive as strings), JSON and array casts are JSON values;
 * - translatable attributes (spatie/laravel-translatable, or any model with `getTranslations`/`setTranslations`) are
 *   their translations object (`{"en": …, "dv": …}`); a write takes such an object, or text for the current locale;
 * - a `relation` field is the related record's id: a `BelongsTo` reads and writes its foreign key, other relations
 *   are read only; a dotted name (`programme.name`) reads through relations and is never written.
 */
final class ModelValues
{
    /** Every readable field of a record (not collections), loading the relations they read through. */
    public static function snapshot(EntityDescriptor $entity, Model $model): stdClass
    {
        $values = new stdClass;
        foreach ($entity->fields as $f) {
            if ($f->type === FieldTypes::COLLECTION) {
                continue;
            }
            $values->{$f->key} = self::read($model, $f);
        }

        return $values;
    }

    /**
     * The fields the change hook compares, from the model's current or original attributes (no queries: relations are
     * their foreign key, fields read through other relations are left out, as the .NET interceptor does).
     */
    public static function tracked(EntityDescriptor $entity, Model $model, bool $original): stdClass
    {
        $values = new stdClass;
        foreach ($entity->fields as $f) {
            $attribute = self::attribute($f);
            if ($f->type === FieldTypes::COLLECTION || str_contains($attribute, '.')) {
                continue;
            }
            if ($f->type === FieldTypes::RELATION) {
                $foreignKey = self::foreignKey($model, $attribute);
                if ($foreignKey === null) {
                    continue;
                }
                $id = $original ? $model->getOriginal($foreignKey) : $model->getAttribute($foreignKey);
                $values->{$f->key} = $id === null ? null : Values::text($id);

                continue;
            }
            if (self::isTranslatable($model, $attribute)) {
                $raw = $original ? $model->getRawOriginal($attribute) : $model->getAttributes()[$attribute] ?? null;
                $values->{$f->key} = is_string($raw) ? Json::normalize(Json::decode($raw)) : null;

                continue;
            }
            $values->{$f->key} = self::toValue($original ? $model->getOriginal($attribute) : $model->getAttribute($attribute), $f);
        }

        return $values;
    }

    public static function read(Model $model, FieldDescriptor $f): mixed
    {
        $attribute = self::attribute($f);
        if (str_contains($attribute, '.')) {
            [$first, $rest] = explode('.', $attribute, 2);
            $related = $model->getRelationValue($first);

            return $related instanceof Model ? self::read($related, new FieldDescriptor(key: $f->key, type: $f->type, attribute: $rest)) : null;
        }
        if ($f->type === FieldTypes::RELATION) {
            $foreignKey = self::foreignKey($model, $attribute);
            if ($foreignKey !== null) {
                $id = $model->getAttribute($foreignKey);

                return $id === null ? null : Values::text($id);
            }
            $related = $model->getRelationValue($attribute);

            return $related instanceof Model ? Values::text($related->getKey()) : null;
        }
        if (self::isTranslatable($model, $attribute)) {
            return Json::normalize((object) $model->getTranslations($attribute));
        }

        return self::toValue($model->getAttribute($attribute), $f);
    }

    /** Sets one field on a model (not saved); `fieldNotSettable` for fields that cannot be written. */
    public static function write(Model $model, FieldDescriptor $f, mixed $value): void
    {
        $attribute = self::attribute($f);
        if ($f->type === FieldTypes::COLLECTION || str_contains($attribute, '.')) {
            throw new WorkflowException('fieldNotSettable', "{$f->label} cannot be changed by workflows.");
        }
        if ($f->type === FieldTypes::RELATION) {
            $foreignKey = self::foreignKey($model, $attribute) ?? throw new WorkflowException('fieldNotSettable', "{$f->label} cannot be changed by workflows.");
            $model->setAttribute($foreignKey, $value === null ? null : Values::text($value));

            return;
        }
        if (self::isTranslatable($model, $attribute)) {
            if ($value instanceof stdClass || is_array($value)) {
                $model->setTranslations($attribute, (array) Json::toArray($value));
            } else {
                $model->setTranslation($attribute, app()->getLocale(), $value === null ? null : Values::text($value));
            }

            return;
        }
        $model->setAttribute($attribute, self::toAttribute($model, $attribute, $value));
    }

    /** The model attribute (or relation, or dotted path) a field reads. */
    public static function attribute(FieldDescriptor $f): string
    {
        return $f->attribute ?? $f->key;
    }

    /** The foreign key of a `BelongsTo` relation method, or null for other relations and plain attributes. */
    private static function foreignKey(Model $model, string $relation): ?string
    {
        if (! method_exists($model, $relation)) {
            return null;
        }
        $instance = $model->{$relation}();

        return $instance instanceof BelongsTo ? $instance->getForeignKeyName() : null;
    }

    /** @return Relation<Model, Model, mixed>|null */
    public static function relation(Model $model, string $name): ?Relation
    {
        if (! method_exists($model, $name)) {
            return null;
        }
        $relation = $model->{$name}();

        return $relation instanceof Relation ? $relation : null;
    }

    private static function isTranslatable(Model $model, string $attribute): bool
    {
        return method_exists($model, 'isTranslatableAttribute') && method_exists($model, 'getTranslations') && $model->isTranslatableAttribute($attribute);
    }

    /** A PHP value from a model as a workflow value. */
    public static function toValue(mixed $value, FieldDescriptor $f): mixed
    {
        return match (true) {
            $value === null => null,
            $value instanceof BackedEnum => self::scalar($value->value),
            $value instanceof UnitEnum => $value->name,
            $value instanceof DateTimeInterface => $f->type === FieldTypes::DATE
                ? $value->format('Y-m-d')
                : Instant::format((int) $value->format('Uv')),
            $value instanceof Arrayable => Json::normalize($value->toArray()),
            $value instanceof JsonSerializable => Json::normalize(Json::decode(json_encode($value, JSON_THROW_ON_ERROR))),
            is_array($value), $value instanceof stdClass => Json::normalize($value),
            is_string($value) && in_array($f->type, [FieldTypes::NUMBER, FieldTypes::CURRENCY], true) && is_numeric($value) => (float) $value,
            default => self::scalar($value),
        };
    }

    private static function scalar(mixed $value): mixed
    {
        return is_int($value) ? (float) $value : $value;
    }

    /** A workflow value as the model attribute takes it: whole numbers for integer-backed enums, arrays for array casts. */
    private static function toAttribute(Model $model, string $attribute, mixed $value): mixed
    {
        $cast = $model->getCasts()[$attribute] ?? null;
        if ($value === null || $cast === null) {
            return $value instanceof stdClass ? Json::toArray($value) : $value;
        }
        if (is_string($cast) && enum_exists($cast)) {
            if ($value instanceof UnitEnum) {
                return $value;
            }
            $enum = new ReflectionEnum($cast);
            if (! $enum->isBacked()) {
                return constant("{$cast}::".Values::text($value));
            }

            return $cast::from((string) $enum->getBackingType() === 'int' ? (int) $value : Values::text($value));
        }
        if (in_array($cast, ['int', 'integer'], true) && is_float($value) && floor($value) === $value) {
            return (int) $value;
        }

        return $value instanceof stdClass || is_array($value) ? Json::toArray($value) : $value;
    }
}
