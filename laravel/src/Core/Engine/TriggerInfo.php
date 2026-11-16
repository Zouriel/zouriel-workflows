<?php

namespace Zouriel\Workflows\Core\Engine;

use stdClass;
use Zouriel\Forms\Json;

/** What started an instance (spec §3 `trigger.*`). Port of .NET `Engine/RunState.cs` `TriggerInfo`. */
final class TriggerInfo
{
    public function __construct(
        public string $type = '',
        public ?string $field = null,
        public mixed $from = null,
        public mixed $to = null,
        public ?string $transition = null,
        public ?string $actorType = null,
        public ?string $actorId = null,
        public ?string $actorName = null,
        public ?string $at = null,
        public ?string $eventId = null,
        public ?stdClass $data = null,
        public ?string $comment = null,
    ) {}

    /** The `trigger.*` value; `comment` is only present when remarks were given, so triggers without them read exactly as before. */
    public function toValue(): stdClass
    {
        $value = (object) [
            'type' => $this->type, 'field' => $this->field, 'from' => Json::normalize($this->from), 'to' => Json::normalize($this->to),
            'transition' => $this->transition, 'actor' => $this->actorId, 'actorType' => $this->actorType, 'actorName' => $this->actorName,
            'at' => $this->at, 'eventId' => $this->eventId, 'data' => $this->data === null ? null : Json::copy($this->data),
        ];
        if ($this->comment !== null) {
            $value->comment = $this->comment;
        }

        return $value;
    }

    public function toJson(): string
    {
        return Json::encode($this->toValue());
    }

    public static function fromJson(?string $json): self
    {
        if ($json === null || trim($json) === '') {
            return new self;
        }
        $v = Json::decode($json);
        if (! $v instanceof stdClass) {
            return new self;
        }
        $text = fn (string $key) => is_string($v->{$key} ?? null) ? $v->{$key} : null;

        return new self($text('type') ?? '', $text('field'), $v->from ?? null, $v->to ?? null, $text('transition'), $text('actorType'), $text('actor'),
            $text('actorName'), $text('at'), $text('eventId'), ($v->data ?? null) instanceof stdClass ? $v->data : null, $text('comment'));
    }
}
