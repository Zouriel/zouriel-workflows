<?php

namespace Zouriel\Workflows\Core\Changes;

/** A field's value before and after a change (`from` is null for creates). */
final class FieldChange
{
    public function __construct(public mixed $from = null, public mixed $to = null) {}
}
