<?php

namespace Zouriel\Workflows\Core\Model;

use stdClass;
use Zouriel\Workflows\Core\Json\Read;

/** Asks people to approve or reject (outputs `outcome`, `decidedBy`, `comments`, `data`). Port of .NET `ApprovalStep`. */
final class ApprovalStep extends HumanStep
{
    public ApprovalRule $rule;

    /** Separation of duties (null = no rule). */
    public ?MakerChecker $makerChecker = null;

    public ApprovalOutcomes $outcomes;

    public function __construct()
    {
        $this->rule = new ApprovalRule;
        $this->outcomes = new ApprovalOutcomes;
    }

    protected function read(stdClass $value, string $path): void
    {
        parent::read($value, $path);
        $this->rule = Read::model($value, 'rule', $path, 'Zouriel.Workflows.Model.ApprovalRule', ApprovalRule::fromJson(...)) ?? new ApprovalRule;
        $this->makerChecker = Read::model($value, 'makerChecker', $path, 'Zouriel.Workflows.Model.MakerChecker', MakerChecker::fromJson(...));
        $this->outcomes = Read::model($value, 'outcomes', $path, 'Zouriel.Workflows.Model.ApprovalOutcomes', ApprovalOutcomes::fromJson(...)) ?? new ApprovalOutcomes;
    }

    public function children(): array
    {
        return [['outcome:approved', $this->outcomes->approved], ['outcome:rejected', $this->outcomes->rejected], ['outcome:timedOut', $this->outcomes->timedOut]];
    }

    protected function members(): array
    {
        return ['rule' => $this->rule->toJson(), 'makerChecker' => $this->makerChecker?->toJson(), 'outcomes' => $this->outcomes->toJson(), ...parent::members()];
    }
}
