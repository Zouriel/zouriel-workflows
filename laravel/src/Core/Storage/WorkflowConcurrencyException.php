<?php

namespace Zouriel\Workflows\Core\Storage;

use RuntimeException;

/** A row changed under the unit of work (optimistic concurrency); the engine retries the unit. */
final class WorkflowConcurrencyException extends RuntimeException {}
