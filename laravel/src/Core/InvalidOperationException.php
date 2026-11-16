<?php

namespace Zouriel\Workflows\Core;

use RuntimeException;

/**
 * A failure of the app's side of an operation (a refused database write, a failing directory or action). Named
 * after .NET's exception so incident texts ("InvalidOperationException: …", spec fixtures) read the same on every engine.
 */
class InvalidOperationException extends RuntimeException {}
