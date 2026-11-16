<?php

namespace Zouriel\Workflows\Core\Changes;

/** What the change hook should do with one write: the event to add to the outbox (null when nothing workflows care about changed), and initial values to set. */
final readonly class InspectResult
{
    /** @param  array<string, mixed>  $initialValues  lifecycle fields left empty on a new record, with their initial state */
    public function __construct(public ?EntityChange $change = null, public array $initialValues = []) {}
}
