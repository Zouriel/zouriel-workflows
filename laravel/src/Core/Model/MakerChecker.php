<?php

namespace Zouriel\Workflows\Core\Model;

use stdClass;
use Zouriel\Workflows\Core\Json\Read;
use Zouriel\Workflows\Core\Json\Write;

/** Separation of duties on an approval (spec §7 `makerChecker`); an empty object means no rule. Port of .NET `MakerChecker`. */
final class MakerChecker
{
    /** The person who started the run (and the record's initiator) may not approve. */
    public ?bool $excludeInitiator = null;

    /** Nobody who decided the previous approval of the same record or run may decide this one. */
    public ?bool $excludePreviousApprover = null;

    /** Only people who hold this role may decide. */
    public ?string $requiredRole = null;

    public static function fromJson(stdClass $value, string $path): self
    {
        $rule = new self;
        $rule->excludeInitiator = Read::optionalBool($value, 'excludeInitiator', $path);
        $rule->excludePreviousApprover = Read::optionalBool($value, 'excludePreviousApprover', $path);
        $rule->requiredRole = Read::optionalString($value, 'requiredRole', $path);

        return $rule;
    }

    /** True when any of the three rules is set. */
    public function any(): bool
    {
        return $this->excludeInitiator === true || $this->excludePreviousApprover === true || ($this->requiredRole ?? '') !== '';
    }

    public function toJson(): stdClass
    {
        return Write::object(['excludeInitiator' => $this->excludeInitiator, 'excludePreviousApprover' => $this->excludePreviousApprover, 'requiredRole' => $this->requiredRole]);
    }
}
