<?php

use Illuminate\Support\Facades\DB;
use Zouriel\Workflows\Core\Engine\Views\DefinitionSummary;
use Zouriel\Workflows\Tests\Support\App\Application;
use Zouriel\Workflows\Tests\Support\EloquentTestCase;

uses(EloquentTestCase::class);

const WAIT_THEN_NOTE = <<<'JSON'
{ "specVersion": 1, "id": "wf_wait", "key": "waitThenNote", "kind": "automation", "title": "Wait then note", "entity": "application",
  "trigger": { "type": "fieldChanged", "field": "stage", "to": ["checked"] },
  "steps": [ { "id": "w", "type": "wait", "until": { "type": "duration", "duration": "PT1H" } },
             { "id": "n", "type": "setFields", "values": [ { "field": "notes", "value": { "const": "an hour later" } } ] } ] }
JSON;

it('runs committed events and due timers with workflows:tick', function () {
    $this->publish(WAIT_THEN_NOTE);
    $a = Application::query()->create(['title' => 'Bursary', 'stage' => 'new']);
    $a->update(['stage' => 'checked']);

    $this->artisan('workflows:tick', ['--until-idle' => true])->assertSuccessful();
    expect($a->fresh()->notes)->toBeNull()
        ->and(DB::table('wf_bookmarks')->whereNotNull('due_at')->count())->toBe(1);

    $this->clock->advance(2 * 3600000);
    $this->artisan('workflows:tick')->assertSuccessful();
    expect($a->fresh()->notes)->toBe('an hour later')
        ->and(DB::table('wf_outbox')->whereNull('processed_at')->where('kind', 'change')->count())->toBe(1);
});

it('runs rounds with workflows:work until told to stop', function () {
    $this->publish(WAIT_THEN_NOTE);
    $a = Application::query()->create(['title' => 'Bursary', 'stage' => 'new']);
    $a->update(['stage' => 'checked']);
    $this->clock->advance(2 * 3600000);

    // Round 1 starts the run (it waits for an hour from now), so the hour has to pass again.
    $this->artisan('workflows:work', ['--max-rounds' => 1, '--sleep' => 0])->assertSuccessful();
    expect($a->fresh()->notes)->toBeNull();
    $this->clock->advance(2 * 3600000);
    $this->artisan('workflows:work', ['--max-rounds' => 3, '--sleep' => 0])->assertSuccessful();
    expect($a->fresh()->notes)->toBe('an hour later');
});

it('cleans up old processed outbox rows as part of a round', function () {
    $this->publish(WAIT_THEN_NOTE);
    Application::query()->create(['title' => 'Bursary', 'stage' => 'new']);
    $this->artisan('workflows:tick')->assertSuccessful();
    expect(DB::table('wf_outbox')->whereNotNull('processed_at')->count())->toBe(1);

    $this->clock->advance(8 * 86400000);
    // A fresh worker cleans up on its first round.
    $this->artisan('workflows:tick')->assertSuccessful();
    expect(DB::table('wf_outbox')->count())->toBe(0);
});

it('seeds definitions from JSON files and changes nothing when run again', function () {
    $dir = sys_get_temp_dir().'/wf_seed_'.bin2hex(random_bytes(4));
    mkdir($dir);
    file_put_contents("{$dir}/a.json", WAIT_THEN_NOTE);
    file_put_contents("{$dir}/b.json", str_replace(['wf_wait', 'waitThenNote', 'PT1H'], ['wf_wait2', 'waitTwice', 'PT2H'], WAIT_THEN_NOTE));
    try {
        $this->artisan('workflows:seed', ['path' => $dir])->expectsOutputToContain('created and published waitThenNote v1')->assertSuccessful();
        $audit = DB::table('wf_audit')->count();
        $this->artisan('workflows:seed', ['path' => $dir])->expectsOutputToContain('waitThenNote is up to date (v1)')->assertSuccessful();
        expect(DB::table('wf_audit')->count())->toBe($audit);

        file_put_contents("{$dir}/a.json", str_replace('PT1H', 'PT3H', WAIT_THEN_NOTE));
        $this->artisan('workflows:seed', ['path' => "{$dir}/a.json"])->expectsOutputToContain('published waitThenNote v2')->assertSuccessful();
        $versions = array_map(fn (DefinitionSummary $d) => [$d->key, $d->version], $this->engine()->listDefinitions());
        sort($versions);
        expect($versions)->toBe([['waitThenNote', 2], ['waitTwice', 1]]);

        file_put_contents("{$dir}/a.json", str_replace('"duration": "PT1H"', '"duration": "soon"', WAIT_THEN_NOTE));
        $this->artisan('workflows:seed', ['path' => "{$dir}/a.json"])->assertFailed();
        $this->artisan('workflows:seed', ['path' => "{$dir}/missing.json"])->assertFailed();
    } finally {
        array_map(unlink(...), glob("{$dir}/*"));
        rmdir($dir);
    }
});
