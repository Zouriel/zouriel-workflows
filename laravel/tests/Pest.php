<?php

use Zouriel\Forms\Json;
use Zouriel\Workflows\Core\Host\WorkflowUser;
use Zouriel\Workflows\Core\Registry\WorkflowRegistry;
use Zouriel\Workflows\Core\Testing\WorkflowTestHost;
use Zouriel\Workflows\Tests\Laravel\Http\ApiTestCase;

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

/**
 * A test host with the shared registry and users of spec/fixtures/engine, optionally with application "a1" created
 * and settled (as .NET's EngineBehaviourTests HostAsync / WithRecordAsync).
 */
function engineHost(?array $record = null): WorkflowTestHost
{
    $dir = __DIR__.'/../../spec/fixtures/engine';
    $host = new WorkflowTestHost(WorkflowRegistry::fromJson(file_get_contents("{$dir}/_registry.json")));
    foreach (Json::decode(file_get_contents("{$dir}/_users.json")) as $u) {
        $host->identity->add(new WorkflowUser($u->id, $u->name, $u->email ?? null, $u->roles ?? []), $u->manager ?? null);
    }
    if ($record !== null) {
        $host->store->createRecord('application', 'a1', (object) $record);
        $host->settle();
    }

    return $host;
}

uses(ApiTestCase::class)->in('Laravel/Http');
