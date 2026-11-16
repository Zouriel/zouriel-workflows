<?php

namespace Zouriel\Workflows\Core\Engine;

use stdClass;
use Zouriel\Workflows\Core\Time\WorkCalendar;

/**
 * The lifecycle-owned fields and, for fields where the lifecycle depends on the record (several lifecycles,
 * or an "applies to" condition), which lifecycle each record is pinned to. The change hook reads it while
 * the app saves. Port of .NET `WorkflowEngine.OwnershipMap`.
 */
final class OwnershipMap
{
    /** @var list<OwnedField> in selection order */
    public readonly array $fields;

    /**
     * @param  list<OwnedField>  $fields
     * @param  array<string, true>  $pinnedPairs  "entity\nfield"
     * @param  array<string, string>  $pins  "entity\nfield\nid" → definition id
     */
    public function __construct(array $fields, public readonly int $loadedAt, public readonly WorkCalendar $calendar, private readonly array $pinnedPairs = [],
        private array $pins = [])
    {
        $this->fields = WorkflowEngine::inSelectionOrder($fields, fn (OwnedField $f) => $f->appliesWhen !== null, fn (OwnedField $f) => $f->publishedAt,
            fn (OwnedField $f) => $f->definitionId);
    }

    /** The same map, marked stale (the pins are kept). */
    public function stale(): self
    {
        return new self($this->fields, PHP_INT_MIN, $this->calendar, $this->pinnedPairs, $this->pins);
    }

    /** Records a record's lifecycle for a field (null: the record has none any more). */
    public function pin(string $entity, string $field, string $id, ?string $definitionId): void
    {
        if (! isset($this->pinnedPairs["{$entity}\n{$field}"])) {
            return;
        }
        if ($definitionId === null) {
            unset($this->pins["{$entity}\n{$field}\n{$id}"]);
        } else {
            $this->pins["{$entity}\n{$field}\n{$id}"] = $definitionId;
        }
    }

    /**
     * The lifecycle that controls `$field` of a record (spec §6 applies to): the one the record is pinned to;
     * for a record with no lifecycle yet, the first whose condition its values meet.
     */
    public function for(string $entity, string $field, string $id, ?stdClass $values, string $today): ?OwnedField
    {
        if (isset($this->pinnedPairs["{$entity}\n{$field}"]) && isset($this->pins["{$entity}\n{$field}\n{$id}"])) {
            $pinned = $this->pins["{$entity}\n{$field}\n{$id}"];

            return array_find($this->fields, fn (OwnedField $f) => $f->definitionId === $pinned && $f->entity === $entity && $f->field === $field);
        }

        return array_find($this->fields, fn (OwnedField $f) => $f->entity === $entity && $f->field === $field
            && LifecycleSelection::applies($f->appliesWhen, $values, $today));
    }
}
