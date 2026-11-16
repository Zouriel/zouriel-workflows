<?php

namespace Zouriel\Workflows\Core\Host;

/** Resolves the named secrets of webhook connections; secrets never live in settings. Port of .NET `IWorkflowSecretResolver`. */
interface SecretResolver
{
    public function secret(string $name): ?string;
}
