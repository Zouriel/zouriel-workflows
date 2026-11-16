<?php

namespace Zouriel\Workflows\Core\Engine;

use stdClass;
use Zouriel\Forms\Json;

/**
 * Everything an instance needs to continue later, stored as JSON in `InstanceRecord::$data`: tokens,
 * step outputs, variables, input, the deleted record's snapshot, goTo counters, joins and sequential
 * forEach loops. Port of .NET `Engine/RunState.cs`.
 */
final class RunState
{
    public string $rootList = 'root';

    /** @var list<Token> */
    public array $tokens = [];

    /** @var array<string, stdClass> step outputs by step id */
    public array $outputs = [];

    public stdClass $vars;

    public stdClass $input;

    /** For runs about a deleted record: its values just before deletion (what `entity.*` reads). */
    public ?stdClass $snapshot = null;

    /** @var array<string, int> */
    public array $goTo = [];

    /** @var array<string, JoinState> */
    public array $joins = [];

    /** @var array<string, list<stdClass>> sequential forEach items by "token:step" */
    public array $loops = [];

    public int $nextToken = 1;

    public function __construct()
    {
        $this->vars = new stdClass;
        $this->input = new stdClass;
    }

    public function token(string $id): ?Token
    {
        return array_find($this->tokens, fn (Token $t) => $t->id === $id);
    }

    public function newTokenId(): string
    {
        return 't'.$this->nextToken++;
    }

    public function setOutput(string $stepId, string $key, mixed $value): void
    {
        $this->outputs[$stepId] ??= new stdClass;
        $this->outputs[$stepId]->{$key} = Json::normalize($value);
    }

    public function output(string $stepId): ?stdClass
    {
        return $this->outputs[$stepId] ?? null;
    }

    /** The outputs as one object value (step id → outputs). */
    public function outputsValue(): stdClass
    {
        $out = new stdClass;
        foreach ($this->outputs as $stepId => $values) {
            $out->{$stepId} = $values;
        }

        return $out;
    }

    public function toJson(): string
    {
        $map = function (array $items, callable $f): stdClass {
            $out = new stdClass;
            foreach ($items as $key => $item) {
                $out->{$key} = $f($item);
            }

            return $out;
        };

        return Json::encode((object) array_filter([
            'rootList' => $this->rootList,
            'tokens' => array_map(fn (Token $t) => $t->toJson(), $this->tokens),
            'outputs' => $this->outputsValue(),
            'vars' => $this->vars,
            'input' => $this->input,
            'snapshot' => $this->snapshot,
            'goTo' => $map($this->goTo, fn ($n) => $n),
            'joins' => $map($this->joins, fn (JoinState $j) => $j->toJson()),
            'loops' => $map($this->loops, fn ($items) => $items),
            'nextToken' => $this->nextToken,
        ], fn ($v) => $v !== null));
    }

    public static function fromJson(?string $json): self
    {
        $state = new self;
        if ($json === null || trim($json) === '' || $json === '{}') {
            return $state;
        }
        $v = Json::decode($json);
        if (! $v instanceof stdClass || ! isset($v->tokens)) {
            return $state;
        }
        $state->rootList = $v->rootList ?? 'root';
        $state->tokens = array_map(Token::fromJson(...), $v->tokens ?? []);
        foreach (get_object_vars($v->outputs ?? new stdClass) as $key => $values) {
            $state->outputs[(string) $key] = $values instanceof stdClass ? $values : new stdClass;
        }
        $state->vars = $v->vars ?? new stdClass;
        $state->input = $v->input ?? new stdClass;
        $state->snapshot = $v->snapshot ?? null;
        foreach (get_object_vars($v->goTo ?? new stdClass) as $key => $n) {
            $state->goTo[(string) $key] = (int) $n;
        }
        foreach (get_object_vars($v->joins ?? new stdClass) as $key => $join) {
            $state->joins[(string) $key] = JoinState::fromJson($join);
        }
        foreach (get_object_vars($v->loops ?? new stdClass) as $key => $items) {
            $state->loops[(string) $key] = $items;
        }
        $state->nextToken = (int) ($v->nextToken ?? 1);

        return $state;
    }
}
