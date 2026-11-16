<?php

namespace Zouriel\Workflows\Laravel\Adapters;

use Illuminate\Contracts\Auth\Factory;
use Zouriel\Workflows\Core\Host\ActorAccessor;
use Zouriel\Workflows\Core\Host\WorkflowActor;

/** The signed-in user (`Auth::user()`) as the actor of record writes made outside workflows; its `name` attribute is the name shown. */
final class AuthActorAccessor implements ActorAccessor
{
    public function __construct(private readonly Factory $auth) {}

    public function current(): ?WorkflowActor
    {
        $user = $this->auth->guard()->user();
        if ($user === null) {
            return null;
        }
        $name = method_exists($user, 'getAttribute') ? $user->getAttribute('name') : null;

        return WorkflowActor::user((string) $user->getAuthIdentifier(), is_string($name) ? $name : null);
    }
}
