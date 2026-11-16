<?php

namespace Zouriel\Workflows\Laravel\Registry;

use Illuminate\Database\Eloquent\Model;
use Zouriel\Workflows\Core\WorkflowException;

/** Which Eloquent model stores each registered entity, and back (subclasses of a registered model belong to its entity). */
final class ModelBindings
{
    /** @var array<class-string, string|null> */
    private array $byClass = [];

    /** @param  array<string, class-string<Model>>  $models  entity key => model class */
    public function __construct(public readonly array $models) {}

    /** @return class-string<Model> */
    public function modelOf(string $entity): string
    {
        return $this->models[$entity] ?? throw new WorkflowException('unknownEntity', "'{$entity}' is not bound to an Eloquent model.");
    }

    /** The entity a model belongs to (its class or a parent class is registered), or null. */
    public function entityOf(Model $model): ?string
    {
        $class = $model::class;
        if (array_key_exists($class, $this->byClass)) {
            return $this->byClass[$class];
        }
        $found = null;
        for ($c = $class; $c !== false && $found === null; $c = get_parent_class($c)) {
            $found = array_search($c, $this->models, true) ?: null;
        }

        return $this->byClass[$class] = $found;
    }
}
