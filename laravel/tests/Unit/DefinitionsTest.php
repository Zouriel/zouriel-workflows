<?php

use Zouriel\Workflows\Core\Model\ApprovalStep;
use Zouriel\Workflows\Core\Model\ConditionGroup;
use Zouriel\Workflows\Core\Model\ConditionRule;
use Zouriel\Workflows\Core\Model\Definitions;
use Zouriel\Workflows\Core\Model\NotifyStep;
use Zouriel\Workflows\Core\Model\TemplateRef;
use Zouriel\Workflows\Core\Model\UnknownStep;

// Expectations from .NET Model/Converters.cs and its tests (SeparationOfDutiesTests, SlaPauseTests).

function definitionJson(string $steps = '[]', string $extra = ''): string
{
    return '{ "specVersion": 1, "id": "w", "key": "w", "kind": "automation", "title": "W"'.$extra.', "steps": '.$steps.' }';
}

it('refuses what .NET refuses, with its messages', function (string $json, string $message) {
    expect(fn () => Definitions::parse($json))->toThrow(JsonException::class, $message);
})->with([
    'null' => ['null', 'Empty definition.'],
    'a step that is not an object' => [definitionJson('[1]'), 'A step must be an object.'],
    'onError that is a number' => [definitionJson('[{ "id": "s", "type": "end", "onError": 3 }]'), 'onError must be a string or an object.'],
    'a template that is an object' => [definitionJson('[{ "id": "s", "type": "end", "reason": {} }]'), 'A template must be a string or an array.'],
    'a condition that is a list' => [definitionJson('[]', ', "trigger": { "type": "created", "when": [] }'), 'A condition must be an object.'],
    'an operand that is text' => [definitionJson('[]', ', "trigger": { "type": "created", "when": { "ref": "entity.a", "op": "eq", "value": "x" } }'), 'An operand must be an object.'],
    'a calculation argument that is a number' => [definitionJson('[{ "id": "s", "type": "setFields", "values": [ { "field": "a", "value": { "calc": { "op": "sum", "args": [1] } } } ] }]'), 'A calculation argument must be an object.'],
    'a title that is a number' => ['{ "title": 5 }', 'could not be converted to System.String. Path: $.title'],
    'a max that is a fraction' => [definitionJson('[{ "id": "s", "type": "forEach", "max": 1.5 }]'), 'Path: $.steps[0].max'],
    'not JSON' => ['{ nope', 'Syntax error'],
]);

it('keeps an unknown step type with only its id, type and title', function () {
    $step = Definitions::parse(definitionJson('[{ "id": "s", "type": "teleport", "title": "Beam", "note": "x" }]'))->steps[0];

    expect($step)->toBeInstanceOf(UnknownStep::class)
        ->and([$step->id, $step->type, $step->title, $step->note])->toBe(['s', 'teleport', 'Beam', null]);
});

it('reads operands, templates and conditions as the converters do', function () {
    $def = Definitions::parse(definitionJson('[{ "id": "n", "type": "notify", "channel": "email", "to": [], "body": ["Hi ", { "ref": "entity.title" }, 3] }]',
        ', "trigger": { "type": "created", "when": { "conditions": [ { "ref": "entity.a", "value": { "const": 1, "ref": "x" } }, 7, { "conditions": [] } ] } }'));
    $when = $def->trigger->when;
    $rule = $when->conditions[0];

    expect($when)->toBeInstanceOf(ConditionGroup::class)
        ->and($when->match)->toBe('all')
        ->and($when->conditions)->toHaveCount(2)
        ->and($rule)->toBeInstanceOf(ConditionRule::class)
        ->and($rule->op)->toBe('eq')
        ->and([$rule->value->hasConst, $rule->value->const, $rule->value->ref])->toBe([true, 1.0, null])
        ->and($def->steps[0])->toBeInstanceOf(NotifyStep::class)
        ->and($def->steps[0]->body->parts)->toEqual(['Hi ', new TemplateRef('entity.title')]);
});

it('treats an empty makerChecker as no rule', function () {
    $step = Definitions::parse(definitionJson('[{ "id": "a", "type": "approval", "assignees": [], "due": "P1D", "makerChecker": {} }]'))->steps[0];

    expect($step)->toBeInstanceOf(ApprovalStep::class)->and($step->makerChecker->any())->toBeFalse();
});

it('round-trips assignees and the public processing time without extra keys', function () {
    $json = Definitions::serialize(Definitions::parse(definitionJson(
        '[{ "id": "n", "type": "notify", "channel": "email", "to": [ { "queue": "frontDesk" }, { "portal": "staff" }, { "department": "licensing" } ] }]',
        ', "publicProcessingTime": { "estimate": "5 days", "display": true }',
    )));

    expect($json)->toContain('"publicProcessingTime":{"estimate":"5 days","display":true}')
        ->toContain('{"queue":"frontDesk"}')
        ->not->toContain('makerChecker')
        ->and(Definitions::serialize(Definitions::parse(definitionJson())))->not->toContain('publicProcessingTime');
});

it('reads and writes the SLA pause lists as plain JSON arrays', function () {
    $lifecycle = fn (string $pause) => '{ "specVersion": 1, "id": "lc", "key": "lc", "kind": "lifecycle", "title": "L", "entity": "application",
        "lifecycle": { "field": "status", "states": [ { "id": "r", "key": "underReview", "label": "R", "sla": { "within": "P5D", '.$pause.'"steps": [] } } ], "transitions": [] } }';
    $def = Definitions::parse($lifecycle('"pauseOnStatus": ["w"], "pauseOnEvent": ["payment.received"], "resumeOnEvent": ["nightly"], '));
    $sla = $def->lifecycle->states[0]->sla;

    expect([$sla->pauseOnStatus, $sla->pauseOnEvent, $sla->resumeOnEvent])->toBe([['w'], ['payment.received'], ['nightly']])
        ->and(Definitions::serialize($def))->toContain('"pauseOnStatus":["w"]')
        ->and(Definitions::serialize(Definitions::parse($lifecycle(''))))->not->toContain('pauseOnStatus');
});

it('fills the defaults the .NET model has', function () {
    $def = Definitions::parse('{ "steps": [ { "id": "f", "type": "forEach" }, { "id": "t", "type": "task" }, { "id": "w", "type": "webhook" } ],
        "lifecycle": { "transitions": [ { "id": "t1", "from": "*", "input": {} } ] } }');

    expect([$def->specVersion, $def->kind, $def->steps[0]->max, $def->steps[1]->onTimeout->id, $def->steps[2]->method])->toBe([1, 'automation', 100, '', 'POST'])
        ->and($def->lifecycle->transitions[0]->from->any)->toBeTrue()
        ->and($def->lifecycle->transitions[0]->input->form)->toEqual(new stdClass);
});
