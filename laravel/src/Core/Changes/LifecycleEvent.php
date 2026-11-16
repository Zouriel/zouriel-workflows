<?php

namespace Zouriel\Workflows\Core\Changes;

use stdClass;
use Zouriel\Forms\Json;
use Zouriel\Workflows\Core\Host\WorkflowActor;
use Zouriel\Workflows\Core\Time\Instant;

/** A lifecycle move carried through the outbox to start `stateEntered`/`transitionApplied` automations. Port of .NET `LifecycleEvent`. */
final class LifecycleEvent
{
    public string $eventId;

    public string $lifecycle = '';

    public string $entity = '';

    public string $entityId = '';

    public ?string $fromState = null;

    public string $toState = '';

    /** The transition key, or null for a forced state or reset. */
    public ?string $transition = null;

    public ?string $actorType = null;

    public ?string $actorId = null;

    public ?string $actorName = null;

    public int $depth = 0;

    public ?string $causationDefinitionId = null;

    public ?string $correlationId = null;

    public int $at = 0;

    public ?string $comment = null;

    public function __construct()
    {
        $this->eventId = bin2hex(random_bytes(16));
    }

    public function actor(): WorkflowActor
    {
        return $this->actorType === 'user' && $this->actorId !== null
            ? WorkflowActor::user($this->actorId, $this->actorName)
            : new WorkflowActor($this->actorType ?? 'system', $this->actorId, $this->actorName);
    }

    public function toJson(): string
    {
        $members = get_object_vars($this);
        $members['at'] = Instant::format($this->at);

        return Json::encode((object) $members);
    }

    public static function fromJson(string $json): self
    {
        /** @var stdClass $v */
        $v = Json::decode($json);
        $event = new self;
        foreach (get_object_vars($v) as $key => $value) {
            $event->{$key} = match ($key) {
                'at' => Instant::parse($value) ?? 0,
                'depth' => (int) $value,
                default => $value,
            };
        }

        return $event;
    }
}
