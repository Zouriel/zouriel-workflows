<?php

use Zouriel\Workflows\Core\Expressions\WorkflowConditions;

if (! specAvailable()) {
    test('conformance runs in the zouriel-workflows monorepo, next to spec/')->skip('spec/fixtures not found');

    return;
}

// spec/fixtures/conditions is shared with the form builder. Workflows read token paths and skip
// the form-only operators, exactly as .NET's ExpressionFixtureTests does.

function workflowConditionCases(): array
{
    $out = [];
    foreach (specFixture('conditions/conditions.json')->cases as $case) {
        $out[$case->name] = [$case];
    }

    return $out;
}

it('evaluates the shared condition fixtures with token paths', function (stdClass $case) {
    if (WorkflowConditions::usesFormOnly($case->condition ?? null)) {
        $this->markTestSkipped('form-only operator (visible/complete/rows), which workflows do not have');
    }
    $today = specFixture('conditions/conditions.json')->today;

    expect(WorkflowConditions::evaluate($case->condition ?? null, fn (string $path) => $case->values->{$path} ?? null, $today))->toBe($case->expected);
})->with(workflowConditionCases());

it('never matches a form-only operator, not even "hidden"', function (string $op) {
    $condition = (object) ['ref' => 'x', 'op' => $op, 'value' => (object) ['const' => 1.0]];

    expect(WorkflowConditions::evaluate($condition, fn () => null, '2026-10-11'))->toBeFalse();
})->with(WorkflowConditions::FORM_ONLY);
