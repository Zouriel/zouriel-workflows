<?php

namespace Zouriel\Workflows\Laravel\Adapters;

use Illuminate\Contracts\Config\Repository;
use Zouriel\Workflows\Core\Host\SecretResolver;

/** Webhook secrets from `config('workflows.secrets')` (name => value, usually read from env). */
final class ConfigSecrets implements SecretResolver
{
    public function __construct(private readonly Repository $config) {}

    public function secret(string $name): ?string
    {
        $value = $this->config->get('workflows.secrets', [])[$name] ?? null;

        return is_string($value) ? $value : null;
    }
}
