<?php

use Zouriel\Forms\Json;

/**
 * Whether the shared spec is next to this package. It is in the zouriel-workflows monorepo; the
 * read-only split repo (Zouriel/workflows-laravel) has no spec, so conformance tests skip there.
 */
function specAvailable(): bool
{
    return is_dir(__DIR__.'/../../spec/fixtures');
}

/** The shared spec fixtures every engine must pass (spec/fixtures, the tie-breaker of SPEC.md). */
function specFixture(string $path): stdClass
{
    return Json::decode(file_get_contents(__DIR__."/../../spec/fixtures/{$path}"));
}

/** @return list<string> fixture file names in one spec fixture folder */
function specFixtureFiles(string $folder): array
{
    return array_map('basename', glob(__DIR__."/../../spec/fixtures/{$folder}/*.json"));
}
