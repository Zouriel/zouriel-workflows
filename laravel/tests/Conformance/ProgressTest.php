<?php

if (! specAvailable()) {
    test('conformance runs in the zouriel-workflows monorepo, next to spec/')->skip('spec/fixtures not found');

    return;
}

// Every shared fixture folder with fixtures in it must have a runner here, so a new folder cannot go unnoticed:
// engine runs in EngineTest.php, lint in LintTest.php, conditions in ConditionsTest.php, audit in AuditTest.php
// (docs/plans/go-and-laravel.md §4.1). Each runner takes its file list from disk, so a new fixture in a folder runs too.

it('has a runner for every fixture folder that holds fixtures', function () {
    $folders = array_values(array_filter(array_map('basename', glob(__DIR__.'/../../../spec/fixtures/*', GLOB_ONLYDIR)),
        fn (string $folder) => glob(__DIR__."/../../../spec/fixtures/{$folder}/*.json") !== []));
    sort($folders);

    expect($folders)->toBe(['audit', 'conditions', 'engine', 'lint']);
});
