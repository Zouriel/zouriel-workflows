<?php

use Zouriel\Workflows\Core\Lint\LintContext;
use Zouriel\Workflows\Core\Lint\Linter;
use Zouriel\Workflows\Core\Lint\LintProblem;
use Zouriel\Workflows\Core\Model\Definitions;
use Zouriel\Workflows\Core\Registry\RegistryBuilder;
use Zouriel\Workflows\Core\Settings\GlobalSettings;

// Behaviour of .NET Lint/Linter.cs that spec/fixtures/lint does not pin down, read from the reference code.

/** @return list<string> `code|node|severity` of each problem */
function lintSteps(string $steps, string $settings = '{}'): array
{
    $def = Definitions::parse('{ "specVersion": 1, "id": "w", "key": "w", "kind": "automation", "title": "W", "trigger": { "type": "manual", "label": "Go" }, "steps": '.$steps.' }');
    $problems = Linter::lint($def, new LintContext((new RegistryBuilder)->role('reviewer', 'Reviewer')->build(), GlobalSettings::parse($settings)));

    return array_map(fn (LintProblem $p) => "{$p->code}|{$p->node}|{$p->severity}", $problems);
}

it('counts a due time that does not parse as zero, so every reminder is late', function () {
    expect(lintSteps('[{ "id": "t", "type": "task", "assignees": [ { "role": "reviewer" } ], "due": "soon", "reminders": ["PT1H"],
        "outcomes": [ { "id": "o", "key": "done", "label": "Done" } ], "onTimeout": { "id": "ot", "steps": [] } }]'))
        ->toBe(['badDuration|t|error', 'lateReminder|t|warning']);
});

it('compares a webhook origin with the allowed ones by its lower-cased host and without the default port', function () {
    $settings = '{ "webhooks": { "allowedOrigins": ["https://hooks.example.com"] } }';

    expect(lintSteps('[{ "id": "h", "type": "webhook", "url": "https://HOOKS.example.com:443/x" }]', $settings))->toBe([])
        ->and(lintSteps('[{ "id": "h", "type": "webhook", "url": "https://hooks.example.com:8443/x" }]', $settings))->toBe(['webhookOriginNotAllowed|h|error'])
        ->and(lintSteps('[{ "id": "h", "type": "webhook", "url": "ftp://hooks.example.com" }]', $settings))->toBe(['badUrl|h|error']);
});

it('treats an empty required role as no maker-checker rule', function () {
    $step = Definitions::parse('{ "steps": [ { "id": "a", "type": "approval", "makerChecker": { "requiredRole": "" } } ] }')->steps[0];

    expect($step->makerChecker->any())->toBeFalse();
});
