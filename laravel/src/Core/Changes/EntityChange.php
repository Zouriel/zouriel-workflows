<?php

namespace Zouriel\Workflows\Core\Changes;

use stdClass;
use Zouriel\Forms\Json;
use Zouriel\Workflows\Core\Host\WorkflowActor;
use Zouriel\Workflows\Core\Time\Instant;

/**
 * One committed change to a registered record, carried through the outbox (spec §5). `depth` is 0 for
 * people and schedules, the writing instance's depth + 1 for workflow writes; `engineFields` are fields
 * the engine itself wrote (lifecycle moves, initial states), which lifecycles skip; `suppressTriggers`
 * marks a reset's undo, which starts no automations. Port of .NET `Changes/EntityChange.cs`.
 */
final class EntityChange
{
    public string $eventId;

    public string $kind = ChangeKinds::UPDATED;

    public string $entity = '';

    public string $entityId = '';

    /** @var array<string, FieldChange> changed watchable fields */
    public array $fields = [];

    /** For deletes: the record's values just before deletion. */
    public ?stdClass $snapshot = null;

    public ?string $actorType = null;

    public ?string $actorId = null;

    public ?string $actorName = null;

    public int $depth = 0;

    public ?string $causationInstanceId = null;

    public ?string $causationDefinitionId = null;

    /** @var list<string>|null */
    public ?array $engineFields = null;

    public ?string $correlationId = null;

    public int $at = 0;

    public bool $suppressTriggers = false;

    public function __construct()
    {
        $this->eventId = bin2hex(random_bytes(16));
    }

    /** The actor, rebuilt from the stored fields. */
    public function actor(): WorkflowActor
    {
        return match (true) {
            $this->actorType === 'user' && $this->actorId !== null => WorkflowActor::user($this->actorId, $this->actorName),
            $this->actorType === 'workflow' => new WorkflowActor('workflow', $this->actorId, $this->actorName),
            default => WorkflowActor::system(),
        };
    }

    public function toJson(): string
    {
        $fields = new stdClass;
        foreach ($this->fields as $key => $change) {
            $fields->{$key} = (object) ['from' => $change->from, 'to' => $change->to];
        }

        return Json::encode((object) [
            'eventId' => $this->eventId, 'kind' => $this->kind, 'entity' => $this->entity, 'entityId' => $this->entityId, 'fields' => $fields,
            'snapshot' => $this->snapshot, 'actorType' => $this->actorType, 'actorId' => $this->actorId, 'actorName' => $this->actorName,
            'depth' => $this->depth, 'causationInstanceId' => $this->causationInstanceId, 'causationDefinitionId' => $this->causationDefinitionId,
            'engineFields' => $this->engineFields, 'correlationId' => $this->correlationId, 'at' => Instant::format($this->at),
            'suppressTriggers' => $this->suppressTriggers,
        ]);
    }

    public static function fromJson(string $json): self
    {
        /** @var stdClass $v */
        $v = Json::decode($json);
        $change = new self;
        $change->eventId = $v->eventId;
        $change->kind = $v->kind;
        $change->entity = $v->entity;
        $change->entityId = $v->entityId;
        foreach (get_object_vars($v->fields ?? new stdClass) as $key => $f) {
            $change->fields[(string) $key] = new FieldChange($f->from ?? null, $f->to ?? null);
        }
        $change->snapshot = $v->snapshot ?? null;
        $change->actorType = $v->actorType ?? null;
        $change->actorId = $v->actorId ?? null;
        $change->actorName = $v->actorName ?? null;
        $change->depth = (int) ($v->depth ?? 0);
        $change->causationInstanceId = $v->causationInstanceId ?? null;
        $change->causationDefinitionId = $v->causationDefinitionId ?? null;
        $change->engineFields = $v->engineFields ?? null;
        $change->correlationId = $v->correlationId ?? null;
        $change->at = Instant::parse($v->at) ?? 0;
        $change->suppressTriggers = (bool) ($v->suppressTriggers ?? false);

        return $change;
    }
}
