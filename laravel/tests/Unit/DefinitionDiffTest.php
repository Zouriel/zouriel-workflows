<?php

use Zouriel\Workflows\Core\Lint\DefinitionDiff;
use Zouriel\Workflows\Core\Model\Definitions;

// Expectations from .NET Lint/DefinitionDiff.cs and EngineBehaviourTests (Identical_versions_have_no_differences).

function diffOf(string $before, string $after): array
{
    return DefinitionDiff::describe(Definitions::parse($before), Definitions::parse($after));
}

function lifecycleVersion(string $title = 'L', string $stateLabel = 'Draft', string $transition = '"who": [ { "role": "reviewer" } ]', string $extra = ''): string
{
    return '{ "specVersion": 1, "id": "lc", "key": "lc", "kind": "lifecycle", "title": "'.$title.'", "entity": "application"'.$extra.',
      "lifecycle": { "field": "status", "states": [ { "id": "d", "key": "draft", "label": "'.$stateLabel.'", "category": "initial" }, { "id": "x", "key": "approved", "label": "Done", "category": "terminal" } ],
        "transitions": [ { "id": "t1", "key": "finish", "label": "Finish", "from": ["d"], "to": "x", '.$transition.' } ] } }';
}

it('finds no differences between identical versions', function () {
    $same = '{ "specVersion": 1, "id": "wf_s", "key": "same", "kind": "automation", "title": "Same", "trigger": { "type": "manual" }, "steps": [] }';

    expect(diffOf($same, $same))->toBe([]);
});

it('describes added, removed, moved and changed steps once each', function () {
    $before = '{ "id": "w", "title": "W", "trigger": { "type": "created" }, "steps": [
        { "id": "b", "type": "branch", "title": "Check", "paths": [ { "id": "p", "label": "P", "steps": [ { "id": "e", "type": "end", "reason": "old" } ] } ] },
        { "id": "n", "type": "setVariables", "values": [] } ] }';
    $after = '{ "id": "w", "title": "W2", "trigger": { "type": "created" }, "steps": [
        { "id": "n", "type": "setVariables", "values": [] },
        { "id": "b", "type": "branch", "title": "Check", "paths": [ { "id": "p", "label": "P", "steps": [ { "id": "e", "type": "end", "reason": "new" } ] } ] },
        { "id": "g", "type": "goTo", "target": "n" } ] }';

    expect(diffOf($before, $after))->toBe([
        'Renamed from “W” to “W2”.',
        'Added step “Go back”.',
        'Moved step “Set variables”.',
        'Moved step “Check”.',
        'Changed step “End”.',
    ]);
});

it('describes lifecycle changes, and says when only wording changed', function () {
    expect(diffOf(lifecycleVersion(), lifecycleVersion(stateLabel: 'New')))->toBe(['Renamed state “Draft” to “New”.'])
        ->and(diffOf(lifecycleVersion(), lifecycleVersion(transition: '"who": [ { "role": "admin" } ]')))->toBe(['Changed who can do or what happens on “Finish”.'])
        ->and(diffOf(lifecycleVersion(), lifecycleVersion(transition: '"who": [ { "role": "reviewer" } ], "tone": "danger"')))->toBe(['Only layout or wording changes.'])
        ->and(diffOf(lifecycleVersion(), lifecycleVersion(extra: ', "unknownToTheModel": 1')))->toBe([]);
});
