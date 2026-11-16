<?php

namespace Zouriel\Workflows\Laravel\Http;

use Illuminate\Http\Request;
use Zouriel\Workflows\Core\Host\WorkflowActor;

/**
 * Who is calling the workflow API, as a workflow actor (id, name, roles: roles count for `who: [{ role }]`). Bind your
 * own to map the app's users; the default is {@see AuthActorResolver}. Port of .NET `HttpContextActorAccessor`.
 */
interface ActorResolver
{
    /** The signed-in person behind the request, or null (not signed in, or a principal without a user id). */
    public function resolve(Request $request): ?WorkflowActor;
}
