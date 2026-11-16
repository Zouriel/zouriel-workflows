<?php

namespace Zouriel\Workflows\Laravel\Console;

use Illuminate\Console\Command;
use JsonException;
use Zouriel\Forms\Json;
use Zouriel\Workflows\Core\Engine\Views\DefinitionSummary;
use Zouriel\Workflows\Core\Engine\WorkflowEngine;
use Zouriel\Workflows\Core\Host\WorkflowActor;
use Zouriel\Workflows\Core\Json\Canonical;
use Zouriel\Workflows\Core\Lint\LintProblem;
use Zouriel\Workflows\Core\Model\Definitions;
use Zouriel\Workflows\Core\WorkflowException;

/**
 * Creates and publishes workflow definitions from JSON files (one file, or every `*.json` in a folder, by name). Running
 * it again changes nothing for files whose definition is already published as they are; a changed file becomes a new
 * draft and version. Lint errors refuse that file and the command fails.
 */
final class SeedCommand extends Command
{
    protected $signature = 'workflows:seed {path : A definition JSON file, or a folder of them}';

    protected $description = 'Create and publish workflow definitions from JSON files (idempotent)';

    public function handle(WorkflowEngine $engine): int
    {
        $path = (string) $this->argument('path');
        $files = is_dir($path) ? glob(rtrim($path, '/').'/*.json') : [$path];
        if ($files === [] || ! array_all($files, fn (string $file) => is_file($file))) {
            $this->error("No definition JSON found at {$path}.");

            return self::FAILURE;
        }
        sort($files);
        $actor = WorkflowActor::user('system', 'Seeder');
        $failed = false;
        foreach ($files as $file) {
            try {
                $this->line(basename($file).': '.$this->seed($engine, (string) file_get_contents($file), $actor));
            } catch (WorkflowException|JsonException $e) {
                $failed = true;
                $problems = $e instanceof WorkflowException && is_array($e->details)
                    ? ' '.implode(' ', array_map(fn ($p) => $p instanceof LintProblem ? "[{$p->code}] {$p->message}" : '', $e->details)) : '';
                $this->error(basename($file).': '.$e->getMessage().$problems);
            }
        }

        return $failed ? self::FAILURE : self::SUCCESS;
    }

    private function seed(WorkflowEngine $engine, string $json, WorkflowActor $actor): string
    {
        $definition = Definitions::parse($json);
        $existing = array_find($engine->listDefinitions(), fn (DefinitionSummary $d) => $d->id === $definition->id);
        if ($existing === null) {
            $engine->createDefinition($json, $actor);
            $engine->publish($definition->id, 'Seeded', $actor);

            return "created and published {$definition->key} v1";
        }
        $detail = $engine->getDefinition($definition->id);
        if ($existing->version > 0 && ! $existing->hasDraftChanges && self::content($detail->draft) === self::content(Json::decode($json))) {
            return "{$definition->key} is up to date (v{$existing->version})";
        }
        $saved = $engine->saveDraft($definition->id, $json, $actor);
        if ($saved->version > 0 && ! $saved->hasDraftChanges) {
            return "{$definition->key} is up to date (v{$saved->version})";
        }
        $published = $engine->publish($definition->id, 'Seeded', $actor);

        return "published {$definition->key} v{$published->version}";
    }

    /** A definition's canonical JSON without its `revision`, which publishing sets to the version. */
    private static function content(\stdClass $definition): string
    {
        $copy = Json::copy($definition);
        unset($copy->revision);

        return Canonical::encode($copy);
    }
}
