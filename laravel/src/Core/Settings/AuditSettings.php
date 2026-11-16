<?php

namespace Zouriel\Workflows\Core\Settings;

use stdClass;
use Zouriel\Workflows\Core\Json\Read;
use Zouriel\Workflows\Core\Json\Write;

/** Audit retention and masking (spec §11). Port of .NET `AuditSettings`. */
final class AuditSettings
{
    /** @var array<string, int>|null days to keep entries, per category */
    public ?array $retentionDays = null;

    /** @var list<string>|null extra values to mask: `entity.field` and `input.key` */
    public ?array $maskedFields = null;

    public static function fromJson(stdClass $value, string $path): self
    {
        $audit = new self;
        $audit->retentionDays = Read::map($value, 'retentionDays', $path, Read::toInt(...));
        $audit->maskedFields = Read::strings($value, 'maskedFields', $path);

        return $audit;
    }

    public function toJson(): stdClass
    {
        return Write::object(['retentionDays' => $this->retentionDays === null ? null : (object) $this->retentionDays, 'maskedFields' => $this->maskedFields]);
    }
}
