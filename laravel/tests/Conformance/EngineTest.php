<?php

use Zouriel\Workflows\Core\Testing\ConformanceRunner;

if (! specAvailable()) {
    test('conformance runs in the zouriel-workflows monorepo, next to spec/')->skip('spec/fixtures not found');

    return;
}

// spec/fixtures/engine, run exactly as .NET's EngineFixtureTests: every file not starting with "_" is one fixture,
// played against a fresh in-memory engine by the ConformanceRunner (spec/fixtures/README.md).

it('passes the engine fixture', function (string $file) {
    ConformanceRunner::run(__DIR__."/../../../spec/fixtures/engine/{$file}");
    expect(true)->toBeTrue();
})->with(array_values(array_filter(specFixtureFiles('engine'), fn (string $f) => ! str_starts_with($f, '_'))));
