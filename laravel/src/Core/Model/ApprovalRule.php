<?php

namespace Zouriel\Workflows\Core\Model;

use stdClass;
use Zouriel\Workflows\Core\Json\Read;
use Zouriel\Workflows\Core\Json\Write;

/** How the decisions of an approval's assignees settle it: `any`, `all`, `count` or `percent`. Port of .NET `ApprovalRule`. */
final class ApprovalRule
{
    public string $type = 'any';

    /** For `count`: approvals needed; for `percent`: percent of assignees (rounded up). */
    public ?int $value = null;

    public static function fromJson(stdClass $value, string $path): self
    {
        $rule = new self;
        $rule->type = Read::string($value, 'type', $path, 'any');
        $rule->value = Read::optionalInt($value, 'value', $path);

        return $rule;
    }

    public function toJson(): stdClass
    {
        return Write::object(['type' => $this->type, 'value' => $this->value]);
    }
}
