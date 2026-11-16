<?php

namespace Zouriel\Workflows\Core\Model;

/** A token part of a template. Port of .NET `TemplateRef`. */
final readonly class TemplateRef
{
    public function __construct(public string $ref) {}
}
