<?php

namespace Zouriel\Workflows\Core\Model;

use stdClass;
use Zouriel\Workflows\Core\Json\Write;

/** The steps run for each outcome of an approval. Port of .NET `ApprovalOutcomes`. */
final class ApprovalOutcomes
{
    /** @var list<Step> */
    public array $approved = [];

    /** @var list<Step> */
    public array $rejected = [];

    /** @var list<Step> */
    public array $timedOut = [];

    public static function fromJson(stdClass $value, string $path): self
    {
        $outcomes = new self;
        $outcomes->approved = Step::list($value, 'approved', $path) ?? [];
        $outcomes->rejected = Step::list($value, 'rejected', $path) ?? [];
        $outcomes->timedOut = Step::list($value, 'timedOut', $path) ?? [];

        return $outcomes;
    }

    public function toJson(): stdClass
    {
        return Write::object(['approved' => Write::list($this->approved), 'rejected' => Write::list($this->rejected), 'timedOut' => Write::list($this->timedOut)]);
    }
}
