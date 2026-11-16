<?php

namespace Zouriel\Workflows\Core\Model;

use stdClass;
use Zouriel\Workflows\Core\Json\JsonShapeException;
use Zouriel\Workflows\Core\Json\Read;
use Zouriel\Workflows\Core\Json\Write;

/**
 * Base of every step (spec §7); the concrete class is chosen by `type` (`StepTypes`). Port of .NET
 * `Model/Steps.cs` `Step` and `StepConverter`: a type this engine does not know becomes an
 * `UnknownStep` keeping its id, type and title (lint reports it, running it fails).
 */
abstract class Step
{
    public string $id = '';

    public string $type = '';

    /** The name on the step card; null uses the step type's label. */
    public ?string $title = null;

    public ?string $note = null;

    public ?bool $disabled = null;

    public ?RetryPolicy $retry = null;

    /** ISO duration after which an attempt fails (call-out steps). */
    public ?string $timeout = null;

    /** Null means `stop`. */
    public ?OnError $onError = null;

    public static function fromJson(mixed $value, string $path): self
    {
        if (! $value instanceof stdClass) {
            throw new JsonShapeException("A step must be an object. Path: {$path}.");
        }
        $type = Read::string($value, 'type', $path);
        $class = StepTypes::ALL[$type] ?? null;
        if ($class === null) {
            $step = new UnknownStep;
            $step->id = Read::string($value, 'id', $path);
            $step->type = $type;
            $step->title = Read::optionalString($value, 'title', $path);

            return $step;
        }
        $step = new $class;
        $step->id = Read::string($value, 'id', $path);
        $step->type = $type;
        $step->title = Read::optionalString($value, 'title', $path);
        $step->note = Read::optionalString($value, 'note', $path);
        $step->disabled = Read::optionalBool($value, 'disabled', $path);
        $step->retry = Read::model($value, 'retry', $path, 'Zouriel.Workflows.Model.RetryPolicy', RetryPolicy::fromJson(...));
        $step->timeout = Read::optionalString($value, 'timeout', $path);
        $step->onError = OnError::fromJson(Read::value($value, 'onError'), "{$path}.onError");
        $step->read($value, $path);

        return $step;
    }

    /** @return list<self>|null the step list at `$key`, null when absent */
    public static function list(stdClass $object, string $key, string $path): ?array
    {
        return Read::list($object, $key, $path, self::fromJson(...));
    }

    /** Reads the members of the concrete step type. */
    protected function read(stdClass $value, string $path): void {}

    /**
     * The step lists nested inside this step, with their slot names (spec "list ids").
     *
     * @return list<array{0: string, 1: list<Step>}>
     */
    public function children(): array
    {
        return [];
    }

    /** The step as JSON: the concrete type's members first, then the shared ones, as .NET writes it. */
    public function toJson(): stdClass
    {
        return Write::object([
            ...$this->members(),
            'id' => $this->id, 'type' => $this->type, 'title' => $this->title, 'note' => $this->note, 'disabled' => $this->disabled,
            'retry' => $this->retry?->toJson(), 'timeout' => $this->timeout, 'onError' => $this->onError?->toJson(),
        ]);
    }

    /** @return array<string, mixed> the concrete type's members, for `toJson()` */
    protected function members(): array
    {
        return [];
    }
}
