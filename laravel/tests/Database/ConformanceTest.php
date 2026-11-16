<?php

use Illuminate\Support\Facades\DB;
use Zouriel\Workflows\Core\Testing\ConformanceRunner;
use Zouriel\Workflows\Tests\Support\DatabaseHostStore;
use Zouriel\Workflows\Tests\Support\DatabaseTestCase;

uses(DatabaseTestCase::class);

if (! specAvailable()) {
    test('conformance runs in the zouriel-workflows monorepo, next to spec/')->skip('spec/fixtures not found');

    return;
}

// Every spec/fixtures/engine file again, now against the database store (SQLite in memory, or PostgreSQL with
// DB_CONNECTION=pgsql): records, workflow rows, audit and outbox go through real transactions.

it('passes the engine fixture on the database store', function (string $file) {
    ConformanceRunner::run(__DIR__."/../../../spec/fixtures/engine/{$file}", fn ($registry, $clock) => new DatabaseHostStore(DB::connection(), $registry, $clock));
    expect(true)->toBeTrue();
})->with(array_values(array_filter(specFixtureFiles('engine'), fn (string $f) => ! str_starts_with($f, '_'))));
