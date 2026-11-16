<?php

namespace Zouriel\Workflows\Laravel\Http\Middleware;

use Closure;
use Illuminate\Http\Request;
use Symfony\Component\HttpFoundation\Response;
use Zouriel\Workflows\Core\Host\WorkflowActor;
use Zouriel\Workflows\Laravel\Http\ActorResolver;
use Zouriel\Workflows\Laravel\Http\ApiErrors;
use Zouriel\Workflows\Laravel\Http\ApiOptions;

/**
 * Finds the workflow actor behind the request (the {@see ActorResolver}) and refuses callers who are not signed in
 * with 401, unless the API allows anonymous callers; they then act as `anonymous`, which the engine refuses wherever a
 * person is needed. A principal without a user id counts as not signed in.
 */
final class IdentifyActor
{
    public const string ATTRIBUTE = 'workflows.actor';

    public function __construct(private readonly ActorResolver $actors) {}

    public function handle(Request $request, Closure $next): Response
    {
        $actor = $this->actors->resolve($request);
        if ($actor === null) {
            if (! ApiOptions::fromConfig()->allowAnonymous) {
                return ApiErrors::error('unauthenticated', 'Sign in to use workflows.', 401);
            }
            $actor = new WorkflowActor('anonymous', null, 'Anonymous');
        }
        $request->attributes->set(self::ATTRIBUTE, $actor);

        return $next($request);
    }
}
