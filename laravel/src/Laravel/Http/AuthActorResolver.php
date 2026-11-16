<?php

namespace Zouriel\Workflows\Laravel\Http;

use Closure;
use Illuminate\Contracts\Auth\Authenticatable;
use Illuminate\Http\Request;
use Zouriel\Workflows\Core\Host\WorkflowActor;

/**
 * The default {@see ActorResolver}: the request's authenticated user, with their auth identifier as id. The name is
 * the user's `name` attribute and the roles come from a `workflowRoles()` method on the user model, unless closures
 * say otherwise:
 *
 * ```php
 * $this->app->bind(ActorResolver::class, fn () => new AuthActorResolver(roles: fn (User $u) => $u->roles->pluck('key')->all()));
 * ```
 */
final class AuthActorResolver implements ActorResolver
{
    /**
     * @param  (Closure(Authenticatable): list<string>)|null  $roles
     * @param  (Closure(Authenticatable): ?string)|null  $name
     */
    public function __construct(private readonly ?Closure $roles = null, private readonly ?Closure $name = null) {}

    public function resolve(Request $request): ?WorkflowActor
    {
        $user = $request->user();
        if (! $user instanceof Authenticatable) {
            return null;
        }
        $id = $user->getAuthIdentifier();
        if ($id === null || $id === '') {
            return null;
        }

        return WorkflowActor::user((string) $id, $this->name !== null ? ($this->name)($user) : $user->name ?? null, $this->rolesOf($user));
    }

    /** @return list<string> */
    private function rolesOf(Authenticatable $user): array
    {
        if ($this->roles !== null) {
            return array_values(($this->roles)($user));
        }

        return method_exists($user, 'workflowRoles') ? array_values($user->workflowRoles()) : [];
    }
}
