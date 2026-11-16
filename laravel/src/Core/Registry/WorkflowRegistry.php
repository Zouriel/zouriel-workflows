<?php

namespace Zouriel\Workflows\Core\Registry;

use JsonException;
use stdClass;
use Zouriel\Forms\Json;
use Zouriel\Workflows\Core\Json\Read;
use Zouriel\Workflows\Core\Json\Write;

/**
 * Everything the host app lets workflows read, watch, write and call (spec §2). Definitions may only
 * reference what is listed here. Port of .NET `Registry/Descriptors.cs` `WorkflowRegistry`; build one
 * in code with `RegistryBuilder`, or read its JSON form with `fromJson()`.
 */
final readonly class WorkflowRegistry
{
    /**
     * @param  list<EntityDescriptor>  $entities
     * @param  list<ActionDescriptor>  $actions
     * @param  list<SignalDescriptor>  $signals
     * @param  list<LabelledItem>  $channels
     * @param  list<LabelledItem>  $roles
     * @param  list<LabelledItem>  $groups
     * @param  list<LabelledItem>|null  $departments  left out of the JSON when null
     * @param  list<LabelledItem>|null  $portals  left out of the JSON when null
     * @param  list<LabelledItem>|null  $queues  left out of the JSON when null
     */
    public function __construct(
        public int $specVersion = 1,
        public array $entities = [],
        public array $actions = [],
        public array $signals = [],
        public array $channels = [],
        public array $roles = [],
        public array $groups = [],
        public ?array $departments = null,
        public ?array $portals = null,
        public ?array $queues = null,
    ) {}

    public function entity(?string $key): ?EntityDescriptor
    {
        return $key === null ? null : array_find($this->entities, fn (EntityDescriptor $e) => $e->key === $key);
    }

    /** A field of an entity, or null when either is unknown. */
    public function field(?string $entity, ?string $field): ?FieldDescriptor
    {
        return $this->entity($entity)?->field($field);
    }

    public function action(?string $key): ?ActionDescriptor
    {
        return array_find($this->actions, fn (ActionDescriptor $a) => $a->key === $key);
    }

    public function signal(?string $key): ?SignalDescriptor
    {
        return array_find($this->signals, fn (SignalDescriptor $s) => $s->key === $key);
    }

    /**
     * Reads a registry from its JSON form (spec `schema/registry.schema.json`).
     *
     * @throws JsonException
     */
    public static function fromJson(string|stdClass $json): self
    {
        $value = is_string($json) ? Json::decode($json) : $json;
        if ($value === null) {
            return new self;
        }
        $value = Read::itemObject($value, '$', 'Zouriel.Workflows.Registry.WorkflowRegistry');
        $items = fn (string $key) => Read::list($value, $key, '$', LabelledItem::fromJson(...));

        return new self(
            specVersion: Read::int($value, 'specVersion', '$', 1),
            entities: Read::list($value, 'entities', '$', EntityDescriptor::fromJson(...)) ?? [],
            actions: Read::list($value, 'actions', '$', ActionDescriptor::fromJson(...)) ?? [],
            signals: Read::list($value, 'signals', '$', SignalDescriptor::fromJson(...)) ?? [],
            channels: $items('channels') ?? [],
            roles: $items('roles') ?? [],
            groups: $items('groups') ?? [],
            departments: $items('departments'),
            portals: $items('portals'),
            queues: $items('queues'),
        );
    }

    /** The registry's JSON form, null members left out (.NET `ToJson`). */
    public function toJson(): stdClass
    {
        return $this->members(false);
    }

    /** The registry as `GET /schema` serves it (ASP.NET's web JSON: null members written as null). */
    public function toApiJson(): stdClass
    {
        return $this->members(true);
    }

    /**
     * The registry as editors see it: fields workflows can neither read, watch, set nor own are left out
     * (they could not be used in a definition anyway, and their names may be private). Port of .NET
     * `WorkflowApi.PublicRegistry`.
     */
    public function forEditors(): self
    {
        return new self(
            $this->specVersion,
            array_map(fn (EntityDescriptor $e) => $e->withFields(array_values(array_filter(
                $e->fields,
                fn (FieldDescriptor $f) => $f->isReadable() || $f->isWatchable() || $f->isSettable() || $f->isLifecycle(),
            ))), $this->entities),
            $this->actions, $this->signals, $this->channels, $this->roles, $this->groups, $this->departments, $this->portals, $this->queues,
        );
    }

    private function members(bool $withNulls): stdClass
    {
        $items = fn (?array $list) => $list === null ? null : array_map(fn ($x) => $x->toJson($withNulls), $list);
        $members = [
            'specVersion' => $this->specVersion, 'entities' => $items($this->entities), 'actions' => $items($this->actions),
            'signals' => $items($this->signals), 'channels' => $items($this->channels), 'roles' => $items($this->roles),
            'groups' => $items($this->groups), 'departments' => $items($this->departments), 'portals' => $items($this->portals),
            'queues' => $items($this->queues),
        ];

        return $withNulls ? Write::objectWithNulls($members) : Write::object($members);
    }
}
