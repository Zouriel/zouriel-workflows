<?php

namespace Zouriel\Workflows\Core\Host;

use stdClass;
use Zouriel\Forms\FormSubmission;

/**
 * The default validator: the form engine of `zouriel/forms` (`FormSubmission::check`), exactly as the
 * browser runs it, plus the server's own checks of choices and shapes. Replaces .NET `Forms/FormValidator.cs`.
 */
final class FormsValidator implements FormValidator
{
    public function validate(stdClass $form, stdClass $data, string $today): array
    {
        return FormSubmission::check($form, $data, today: $today)->errors;
    }

    public function clean(stdClass $form, stdClass $data, string $today): stdClass
    {
        return FormSubmission::check($form, $data, today: $today)->values;
    }
}
