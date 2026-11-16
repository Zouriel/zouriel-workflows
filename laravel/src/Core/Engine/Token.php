<?php

namespace Zouriel\Workflows\Core\Engine;

use stdClass;

/**
 * A thread of execution; parallel paths and parallel forEach items run in child tokens. `waiting` says what
 * a waiting token waits for: `task:ID`, `timer`, `signal`, `condition`, `join`, `child:ID`, `retry`, `incident`.
 * Port of .NET `Engine/RunState.cs` `Token`.
 */
final class Token
{
    public const string ACTIVE = 'active';

    public const string WAITING = 'waiting';

    public const string DONE = 'done';

    public const string CANCELLED = 'cancelled';

    /** @param  list<Frame>  $frames */
    public function __construct(
        public string $id = 'root',
        public array $frames = [],
        public ?string $parentId = null,
        public ?string $joinKey = null,
        public ?string $branch = null,
        public string $status = self::ACTIVE,
        public ?string $waiting = null,
        public ?string $stepRecord = null,
        public ?string $incidentError = null,
        public ?string $incidentStepId = null,
    ) {}

    public function top(): Frame
    {
        return $this->frames[count($this->frames) - 1];
    }

    /** @return array{0: ?stdClass, 1: ?int} the innermost forEach item visible to this token, and its index */
    public function currentItem(): array
    {
        for ($i = count($this->frames) - 1; $i >= 0; $i--) {
            if ($this->frames[$i]->item !== null) {
                return [$this->frames[$i]->item, $this->frames[$i]->itemIndex];
            }
        }

        return [null, null];
    }

    public function isLive(): bool
    {
        return $this->status === self::ACTIVE || $this->status === self::WAITING;
    }

    public function toJson(): stdClass
    {
        return (object) array_filter([
            'id' => $this->id, 'parentId' => $this->parentId, 'joinKey' => $this->joinKey, 'branch' => $this->branch,
            'frames' => array_map(fn (Frame $f) => $f->toJson(), $this->frames), 'status' => $this->status, 'waiting' => $this->waiting,
            'stepRecord' => $this->stepRecord, 'incidentError' => $this->incidentError, 'incidentStepId' => $this->incidentStepId,
        ], fn ($v) => $v !== null);
    }

    public static function fromJson(stdClass $v): self
    {
        return new self($v->id, array_map(Frame::fromJson(...), $v->frames ?? []), $v->parentId ?? null, $v->joinKey ?? null, $v->branch ?? null,
            $v->status ?? self::ACTIVE, $v->waiting ?? null, $v->stepRecord ?? null, $v->incidentError ?? null, $v->incidentStepId ?? null);
    }
}
