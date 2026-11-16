<?php

namespace Zouriel\Workflows\Core\Testing;

use RuntimeException;

/** A conformance fixture step did not produce what it expects; the message names the fixture, the step and the difference. */
final class ConformanceException extends RuntimeException {}
