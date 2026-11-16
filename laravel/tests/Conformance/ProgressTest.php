<?php

if (! specAvailable()) {
    test('conformance runs in the zouriel-workflows monorepo, next to spec/')->skip('spec/fixtures not found');

    return;
}

// The rest of the shared fixtures, one entry each, so the test output shows what the Laravel engine
// does not pass yet. Each `todo` becomes a real test as its part of the engine is ported
// (docs/plans/go-and-laravel.md, Phase 1: engine; Phase 2: storage). Lint runs in LintTest.php.

foreach (specFixtureFiles('engine') as $file) {
    it("engine: {$file}")->todo();
}
