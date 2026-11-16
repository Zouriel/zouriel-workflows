<?php

namespace Zouriel\Workflows\Core\Host;

use stdClass;

/** Checks form answers (transition input, decisions, manual forms) with the form engine's rules. Port of .NET `IFormValidator`. */
interface FormValidator
{
    /**
     * The problems with `$data` (empty when acceptable); `$today` is YYYY-MM-DD.
     *
     * @return list<array{path: string, fieldId: string, message: string}>
     */
    public function validate(stdClass $form, stdClass $data, string $today): array;

    /** The answers the engine keeps once they are valid: shown fields only, calculated fields worked out again. */
    public function clean(stdClass $form, stdClass $data, string $today): stdClass;
}
