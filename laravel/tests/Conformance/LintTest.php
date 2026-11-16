<?php

use Zouriel\Forms\Json;
use Zouriel\Workflows\Core\Lint\LintContext;
use Zouriel\Workflows\Core\Lint\Linter;
use Zouriel\Workflows\Core\Lint\LintProblem;
use Zouriel\Workflows\Core\Model\Definitions;
use Zouriel\Workflows\Core\Registry\WorkflowRegistry;
use Zouriel\Workflows\Core\Settings\GlobalSettings;

if (! specAvailable()) {
    test('conformance runs in the zouriel-workflows monorepo, next to spec/')->skip('spec/fixtures not found');

    return;
}

// spec/fixtures/lint, run exactly as .NET's LintFixtureTests: the sorted `code|node|severity` triples must
// be equal; an expected entry without `node` matches that code on any node; `severity` defaults to error.

/** @return list<string> */
function lintFixtureProblems(string $file): array
{
    $fixture = specFixture("lint/{$file}");
    $registry = WorkflowRegistry::fromJson(file_get_contents(__DIR__."/../../../spec/fixtures/lint/{$fixture->registry}"));
    $settings = GlobalSettings::parse($fixture->settings ?? null);
    $others = array_map(fn (stdClass $o) => Definitions::parse($o), $fixture->others ?? []);
    $problems = Linter::lint(Definitions::parse($fixture->definition), new LintContext($registry, $settings, $others));
    $anyNode = array_map(fn ($e) => $e->code, array_filter($fixture->expected, fn ($e) => ! property_exists($e, 'node')));

    return array_map(fn (LintProblem $p) => $p->code.'|'.(in_array($p->code, $anyNode, true) ? '-' : ($p->node ?? '-')).'|'.$p->severity, $problems);
}

it('reports exactly the expected problems', function (string $file) {
    $fixture = specFixture("lint/{$file}");
    $expected = array_map(fn ($e) => $e->code.'|'.($e->node ?? '-').'|'.($e->severity ?? 'error'), $fixture->expected);
    $actual = lintFixtureProblems($file);
    sort($expected);
    sort($actual);

    expect($actual)->toBe($expected, Json::encode(['expected' => $expected, 'actual' => $actual]));
})->with(specFixtureFiles('lint'));
