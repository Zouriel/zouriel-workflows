<?php

namespace Zouriel\Workflows\Core\Model;

use stdClass;

/** A group of conditions combined by `match` (`all`, `any`, `none`); an empty group holds. Port of .NET `ConditionGroup`. */
final class ConditionGroup extends Condition
{
    public string $match = 'all';

    /** @var list<Condition> */
    public array $conditions = [];

    public function toJson(): stdClass
    {
        return (object) ['match' => $this->match, 'conditions' => array_map(fn (Condition $c) => $c->toJson(), $this->conditions)];
    }

    public function isComplete(): bool
    {
        return array_all($this->conditions, fn (Condition $c) => $c->isComplete());
    }

    public function refs(): array
    {
        return array_merge([], ...array_map(fn (Condition $c) => $c->refs(), $this->conditions));
    }
}
