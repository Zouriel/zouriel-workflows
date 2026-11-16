<?php

namespace Zouriel\Workflows\Core\Model;

/** A step type this engine does not know (from a newer spec). Lint reports it; running it fails. Port of .NET `UnknownStep`. */
final class UnknownStep extends Step {}
