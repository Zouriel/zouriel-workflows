<?php

namespace Zouriel\Workflows\Laravel\Http\Middleware;

use Closure;
use Illuminate\Http\Request;
use Illuminate\Support\Facades\Gate;
use Symfony\Component\HttpFoundation\Response;
use Zouriel\Workflows\Laravel\Http\ApiErrors;
use Zouriel\Workflows\Laravel\Http\ApiOptions;

/**
 * Checks the gate configured for a kind of operation (`workflows.api.gates`, as .NET's per-endpoint policies). No gate
 * set means every signed-in caller may, except for signals: without a signal or intervention gate nobody may raise
 * one through the API, because a signal resumes workflows with data the caller chooses.
 */
final class AuthorizeOperation
{
    public function handle(Request $request, Closure $next, string $kind): Response
    {
        $options = ApiOptions::fromConfig();
        if ($kind === ApiOptions::SIGNAL && $options->gate($kind) === null) {
            return ApiErrors::error('notAllowed', 'Signals cannot be raised through the API here.', 403);
        }
        if (! self::allows($request, $options, $kind)) {
            return ApiErrors::error('notAllowed', 'You are not allowed to do that.', 403);
        }

        return $next($request);
    }

    /** Whether the caller passes the gate of `$kind` (true when none is set). */
    public static function allows(Request $request, ApiOptions $options, string $kind): bool
    {
        $gate = $options->gate($kind);

        return $gate === null || Gate::forUser($request->user())->allows($gate);
    }
}
