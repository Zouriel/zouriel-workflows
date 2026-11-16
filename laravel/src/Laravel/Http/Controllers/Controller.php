<?php

namespace Zouriel\Workflows\Laravel\Http\Controllers;

use Illuminate\Http\Request;
use Illuminate\Routing\Controller as BaseController;
use stdClass;
use Throwable;
use Zouriel\Workflows\Core\Engine\Views\InstanceView;
use Zouriel\Workflows\Core\Engine\Views\Page;
use Zouriel\Workflows\Core\Engine\Views\TaskView;
use Zouriel\Workflows\Core\Engine\WorkflowEngine;
use Zouriel\Workflows\Core\Host\WorkflowActor;
use Zouriel\Workflows\Core\WorkflowException;
use Zouriel\Workflows\Laravel\Http\AccessPolicy;
use Zouriel\Workflows\Laravel\Http\AllowAllAccessPolicy;
use Zouriel\Workflows\Laravel\Http\ApiErrors;
use Zouriel\Workflows\Laravel\Http\ApiOptions;
use Zouriel\Workflows\Laravel\Http\Middleware\IdentifyActor;
use Zouriel\Workflows\Laravel\Http\RequestBody;

/**
 * What every workflow API controller shares: the engine, the access policy, the caller as a workflow actor and the
 * error mapping (engine errors become `{ code, message, details }` here, so they never reach the app's handler).
 * Port of the helpers of .NET's `WorkflowApi`.
 */
abstract class Controller extends BaseController
{
    /** How many matches a filtered list checks against a restrictive access policy before estimating the rest. */
    private const int ACCESS_SCAN_LIMIT = 5000;

    private const int ACCESS_SCAN_BATCH = 250;

    public function __construct(protected readonly WorkflowEngine $engine, protected readonly AccessPolicy $access) {}

    public function callAction($method, $parameters)
    {
        try {
            return parent::callAction($method, $parameters);
        } catch (Throwable $e) {
            return ApiErrors::render($e) ?? throw $e;
        }
    }

    protected function actor(Request $request): WorkflowActor
    {
        return $request->attributes->get(IdentifyActor::ATTRIBUTE) ?? new WorkflowActor('anonymous', null, 'Anonymous');
    }

    protected function options(): ApiOptions
    {
        return ApiOptions::fromConfig();
    }

    /** A required typed body, capped at the API's body limit. */
    protected function body(Request $request): RequestBody
    {
        return RequestBody::required($request, $this->options()->maxBodyBytes);
    }

    /** An optional typed body, capped at the API's body limit. */
    protected function optionalBody(Request $request): RequestBody
    {
        return RequestBody::optional($request, $this->options()->maxBodyBytes);
    }

    /** The body as JSON text (a definition), capped at the API's body limit. */
    protected function jsonText(Request $request): string
    {
        return RequestBody::jsonText($request, $this->options()->maxBodyBytes);
    }

    /** Form answers and outputs: an absent object is an empty one (.NET `Map`). */
    protected static function answers(?stdClass $values): stdClass
    {
        return $values ?? new stdClass;
    }

    protected function requireRecord(Request $request, string $entity, string $id): void
    {
        if (! $this->access->canViewRecord($this->actor($request), $entity, $id)) {
            throw new WorkflowException('notAllowed', 'You cannot see this record.');
        }
    }

    protected function visibleTask(Request $request, string $id): TaskView
    {
        $me = $this->actor($request);
        $task = $this->engine->getTask($id, $me);
        if (! $this->access->canViewTask($me, $task)) {
            throw new WorkflowException('notAllowed', 'You cannot see this task.');
        }

        return $task;
    }

    protected function visibleInstance(Request $request, string $id): InstanceView
    {
        $view = $this->engine->getInstance($id);
        if (! $this->access->canViewInstance($this->actor($request), $view)) {
            throw new WorkflowException('notAllowed', 'You cannot see this instance.');
        }

        return $view;
    }

    /**
     * One page of the items the caller may see, with a total that counts only visible items. With the allow-all policy
     * the store's own paging is used; otherwise matches are read in batches and checked one by one.
     *
     * @template T
     *
     * @param  callable(int, int): Page<T>  $query  skip, take
     * @param  callable(T): bool  $visible
     * @return Page<T>
     */
    protected function visiblePage(callable $query, callable $visible, int $skip, int $take): Page
    {
        if ($this->access instanceof AllowAllAccessPolicy) {
            return $query($skip, $take);
        }
        $items = [];
        $seen = 0;
        for ($offset = 0; ;) {
            $batch = $query($offset, self::ACCESS_SCAN_BATCH);
            foreach ($batch->items as $item) {
                if (! $visible($item)) {
                    continue;
                }
                if ($seen >= $skip && count($items) < $take) {
                    $items[] = $item;
                }
                $seen++;
            }
            $offset += count($batch->items);
            if (count($batch->items) < self::ACCESS_SCAN_BATCH || $offset >= $batch->total) {
                return new Page($items, $seen);
            }
            if ($offset >= self::ACCESS_SCAN_LIMIT) {
                return new Page($items, $seen + ($batch->total - $offset));
            }
        }
    }

    /** A comma-separated query value as a list (null when absent or blank). @return list<string>|null */
    protected static function split(?string $csv): ?array
    {
        if ($csv === null || trim($csv) === '') {
            return null;
        }
        $parts = array_values(array_filter(array_map(trim(...), explode(',', $csv)), fn (string $s) => $s !== ''));

        return $parts === [] ? null : $parts;
    }

    /** A whole-number query value, or null when absent; anything else is a 400. */
    protected static function queryInt(Request $request, string $name): ?int
    {
        $value = self::queryText($request, $name);
        if ($value === null) {
            return null;
        }
        if (! preg_match('/^-?\d{1,18}$/', trim($value))) {
            throw new WorkflowException('invalidInput', "'{$name}' must be a whole number.");
        }

        return (int) trim($value);
    }

    protected static function queryText(Request $request, string $name): ?string
    {
        $value = $request->query($name);
        if (is_array($value)) {
            throw new WorkflowException('invalidInput', "'{$name}' must be given once.");
        }

        return $value === null || $value === '' ? null : (string) $value;
    }

    protected static function clamp(?int $value, int $default, int $min, int $max): int
    {
        return max($min, min($max, $value ?? $default));
    }
}
