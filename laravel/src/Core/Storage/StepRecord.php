<?php

namespace Zouriel\Workflows\Core\Storage;

/**
 * One execution attempt of a step, for run views. `undo` is the journal entry a reset reads (spec §6):
 * unmasked JSON, never shown as is. Port of .NET `StepRecord`.
 */
final class StepRecord
{
    public string $id = '';

    public string $instanceId = '';

    public string $nodeId = '';

    public string $stepType = '';

    public ?string $tokenId = null;

    public int $attempt = 1;

    public string $status = StepStatus::RUNNING;

    public ?string $input = null;

    public ?string $output = null;

    public ?string $error = null;

    public ?string $undo = null;

    public int $startedAt = 0;

    public ?int $endedAt = null;
}
