<?php

namespace Zouriel\Workflows\Core\Host;

use RuntimeException;

/** A webhook call that never got an answer (network failure or timeout); the step's attempt fails and may be retried (.NET `HttpRequestException`). */
final class WebhookFailedException extends RuntimeException {}
