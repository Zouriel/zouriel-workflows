<?php

namespace Zouriel\Workflows\Laravel\Http;

/**
 * The API's settings from `config('workflows.api')`: the gate per kind of operation and the request limits. Port of
 * .NET `WorkflowApiOptions`.
 */
final readonly class ApiOptions
{
    public const string VIEW = 'view';

    public const string EDIT = 'edit';

    public const string PUBLISH = 'publish';

    public const string INTERVENE = 'intervene';

    public const string AUDIT = 'audit';

    public const string AUDIT_EXPORT = 'audit_export';

    public const string SETTINGS = 'settings';

    public const string SIGNAL = 'signal';

    /** @param  array<string, string|null>  $gates */
    public function __construct(
        public array $gates = [],
        public bool $allowAnonymous = false,
        public int $maxBodyBytes = 2 * 1024 * 1024,
        public int $maxDecisionDataBytes = 256 * 1024,
        public int $maxSimulationEvents = 50,
    ) {}

    public static function fromConfig(): self
    {
        $api = config('workflows.api', []);

        return new self($api['gates'] ?? [], (bool) ($api['allow_anonymous'] ?? false), (int) ($api['max_body_bytes'] ?? 2 * 1024 * 1024),
            (int) ($api['max_decision_data_bytes'] ?? 256 * 1024), (int) ($api['max_simulation_events'] ?? 50));
    }

    /**
     * The gate a kind of operation needs, or null when none is set (every signed-in caller may). Exporting falls back
     * to the audit gate and signals to the intervention gate.
     */
    public function gate(string $kind): ?string
    {
        return match ($kind) {
            self::AUDIT_EXPORT => $this->gates[self::AUDIT_EXPORT] ?? $this->gates[self::AUDIT] ?? null,
            self::SIGNAL => $this->gates[self::SIGNAL] ?? $this->gates[self::INTERVENE] ?? null,
            default => $this->gates[$kind] ?? null,
        };
    }
}
