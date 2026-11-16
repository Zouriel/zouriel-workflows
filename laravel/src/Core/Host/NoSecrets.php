<?php

namespace Zouriel\Workflows\Core\Host;

/** The default: no secrets set up. */
final class NoSecrets implements SecretResolver
{
    public function secret(string $name): ?string
    {
        return null;
    }
}
