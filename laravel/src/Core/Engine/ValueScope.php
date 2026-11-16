<?php

namespace Zouriel\Workflows\Core\Engine;

use DateTimeZone;
use stdClass;
use Zouriel\Forms\CalcContext;
use Zouriel\Forms\Calculations;
use Zouriel\Forms\Conditions;
use Zouriel\Forms\Json;
use Zouriel\Workflows\Core\Expressions\WorkflowConditions;
use Zouriel\Workflows\Core\Json\Values;
use Zouriel\Workflows\Core\Model\Condition;
use Zouriel\Workflows\Core\Model\Operand;
use Zouriel\Workflows\Core\Registry\FieldDescriptor;
use Zouriel\Workflows\Core\Registry\FieldTypes;
use Zouriel\Workflows\Core\Registry\WorkflowRegistry;
use Zouriel\Workflows\Core\Storage\EntityGateway;
use Zouriel\Workflows\Core\Storage\InstanceRecord;
use Zouriel\Workflows\Core\Storage\RecordSnapshot;
use Zouriel\Workflows\Core\Time\Instant;

/**
 * Resolves token paths (spec §3) for one evaluation. Record values are read from the gateway (once per
 * scope); relation hops load the related record. Port of .NET `Engine/ValueScope.cs` (PHP resolves paths
 * as they are read, so there is nothing to prefetch).
 */
final class ValueScope
{
    /** @var array<string, mixed> */
    private array $resolved = [];

    /** @var array<string, ?RecordSnapshot> */
    private array $records = [];

    public function __construct(
        private readonly EntityGateway $gateway,
        private readonly WorkflowRegistry $registry,
        public readonly int $now,
        public readonly string $today,
        public readonly ?string $entity = null,
        public readonly ?string $entityId = null,
        public readonly ?stdClass $entityOverride = null,
        public readonly ?TriggerInfo $trigger = null,
        public readonly ?RunState $state = null,
        public readonly ?stdClass $item = null,
        public readonly ?int $itemIndex = null,
        public readonly ?string $itemEntity = null,
        public readonly ?InstanceRecord $instance = null,
        public readonly ?DateTimeZone $zone = null,
    ) {}

    /** The current item's record id, inside `forEach`. */
    public function itemId(): ?string
    {
        return is_string($this->item->id ?? null) ? $this->item->id : null;
    }

    public function resolve(string $path): mixed
    {
        if (array_key_exists($path, $this->resolved)) {
            return $this->resolved[$path];
        }
        $parts = explode('.', $path);
        $rest = array_slice($parts, 1);
        $value = match ($parts[0]) {
            'entity' => $this->readRecordPath($this->entity, $this->entityId, $this->entityOverride, $rest),
            'item' => count($parts) === 2 && $parts[1] === 'index'
                ? $this->itemIndex
                : $this->readRecordPath($this->itemEntity, $this->itemId(), $this->item, $rest),
            'trigger' => Values::readPath($this->trigger?->toValue(), $rest),
            'input' => Values::readPath($this->state?->input, $rest),
            'steps' => count($parts) >= 3 && $this->state?->output($parts[1]) !== null ? Values::readPath($this->state->output($parts[1]), array_slice($parts, 2)) : null,
            'vars' => count($parts) >= 2 ? Values::readPath($this->state?->vars->{$parts[1]} ?? null, array_slice($parts, 2)) : null,
            'instance' => match ($parts[1] ?? null) {
                'id' => $this->instance?->id,
                'startedAt' => $this->instance === null ? null : Instant::format($this->instance->startedAt),
                'initiator' => $this->instance?->initiatorId,
                default => null,
            },
            'now' => Instant::format($this->now),
            'today' => $this->today,
            default => null,
        };

        return $this->resolved[$path] = Json::normalize($value);
    }

    /** @param  list<string>  $rest */
    private function readRecordPath(?string $entity, ?string $id, ?stdClass $preset, array $rest): mixed
    {
        if ($entity === null || $rest === []) {
            return null;
        }
        $values = $preset;
        if ($values === null) {
            if ($id === null) {
                return null;
            }
            $values = $this->load($entity, $id)?->values;
        }
        if ($values === null) {
            return null;
        }
        $key = $rest[0];
        if ($key === 'id') {
            return $id ?? ($values->id ?? null);
        }
        $field = $this->registry->field($entity, $key);
        if ($field === null || ! $field->isReadable()) {
            return null;
        }
        $value = $values->{$key} ?? null;
        if (count($rest) === 1) {
            return $value;
        }
        if ($field->type === FieldTypes::RELATION && $value !== null && $field->entity !== null) {
            return $this->readRecordPath($field->entity, Values::text($value), null, array_slice($rest, 1));
        }

        return Values::readPath($value, array_slice($rest, 1));
    }

    public function load(string $entity, string $id): ?RecordSnapshot
    {
        $key = "{$entity}\n{$id}";
        if (! array_key_exists($key, $this->records)) {
            $this->records[$key] = $this->gateway->get($entity, $id);
        }

        return $this->records[$key];
    }

    /** The registered field behind an `entity.*` or `item.*` path, for formatting. */
    public function fieldOf(string $path): ?FieldDescriptor
    {
        $parts = explode('.', $path);
        $entity = match ($parts[0]) {
            'entity' => $this->entity,
            'item' => $this->itemEntity,
            default => null,
        };
        if ($entity === null) {
            return null;
        }
        $field = null;
        for ($i = 1; $i < count($parts); $i++) {
            $field = $this->registry->field($entity, $parts[$i]);
            if ($field === null) {
                return null;
            }
            if ($field->type === FieldTypes::RELATION && $i < count($parts) - 1) {
                $entity = $field->entity;
            } elseif ($i < count($parts) - 1) {
                return null;
            }
        }

        return $field;
    }

    /** Forgets cached record values (after the workflow wrote to the record). */
    public function invalidate(): void
    {
        $this->resolved = [];
        $this->records = [];
    }

    /** The value of an operand: a constant, a token, today plus days, or a calculation (spec §3). */
    public function operand(?Operand $operand): mixed
    {
        return match (true) {
            $operand === null => null,
            $operand->hasConst => $operand->const,
            $operand->ref !== null => $this->resolve($operand->ref),
            $operand->today !== null => Conditions::addDays($this->today, $operand->today),
            $operand->calc !== null => Calculations::evaluate($operand->calc->toJson(), $this->calcContext()),
            default => null,
        };
    }

    public function condition(?Condition $condition): bool
    {
        return WorkflowConditions::evaluate($condition?->toJson(), $this->resolve(...), $this->today);
    }

    private function calcContext(): CalcContext
    {
        return new readonly class($this) implements CalcContext
        {
            public function __construct(private ValueScope $scope) {}

            public function values(string $ref): array
            {
                return [$this->scope->resolve($ref)];
            }

            public function rows(string $ref): ?int
            {
                return null;
            }

            public function today(): string
            {
                return $this->scope->today;
            }
        };
    }
}
