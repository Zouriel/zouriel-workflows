<?php

namespace Zouriel\Workflows\Core\Model;

use stdClass;
use Zouriel\Workflows\Core\Json\Read;

/** The input form of a transition or reset (a form-builder form). Port of .NET `TransitionInput`. */
final class TransitionInput
{
    /** The form definition; `{}` when the input names none, null when it is given as null. */
    public ?stdClass $form;

    public function __construct()
    {
        $this->form = new stdClass;
    }

    public static function fromJson(stdClass $value, string $path): self
    {
        $input = new self;
        if (property_exists($value, 'form')) {
            $input->form = Read::object($value, 'form', $path);
        }

        return $input;
    }

    public function toJson(): stdClass
    {
        return $this->form === null ? new stdClass : (object) ['form' => $this->form];
    }
}
