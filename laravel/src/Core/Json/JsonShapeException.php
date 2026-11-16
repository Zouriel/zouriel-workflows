<?php

namespace Zouriel\Workflows\Core\Json;

use JsonException;

/**
 * JSON that is valid text but not the expected document (a definition, registry or settings): the
 * errors .NET's System.Text.Json raises while deserializing (`JsonException`), so callers catch both
 * kinds as one. The engine reports them as `invalidJson` ("The definition is not valid JSON: …").
 */
final class JsonShapeException extends JsonException {}
