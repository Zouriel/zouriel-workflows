<?php

namespace Zouriel\Workflows\Tests\Laravel\Http;

use Illuminate\Testing\TestResponse;
use Opis\JsonSchema\Errors\ErrorFormatter;
use Opis\JsonSchema\Validator;
use PHPUnit\Framework\Assert;
use stdClass;
use Symfony\Component\HttpFoundation\StreamedResponse;

/**
 * spec/api/openapi.json as a test oracle (plan 4.2): every response an API test receives is matched to its operation
 * and validated against the documented schema; error statuses an operation does not document are validated against
 * `WorkflowError`. Two artefacts of the .NET export are relaxed before validating: `oneOf` with a `JsonNode` (which
 * matches anything, null included) becomes `anyOf`, and `WorkflowError.details` is not required, because .NET writes
 * errors with its document options and leaves a null `details` out. And the registry's optional `departments`,
 * `portals` and `queues` may be null: .NET types them `List<LabelledItem>?` and `GET /schema` writes the null.
 * (All three are spec fixes for Phase 0.)
 */
final class OpenApiContract
{
    public const string BASE = 'https://zouriel.test/openapi.json';

    /** @var array<string, array<string, true>> operation ("GET /api/workflows/...") => statuses seen */
    public static array $seen = [];

    private static ?stdClass $doc = null;

    private static ?Validator $validator = null;

    public static function document(): stdClass
    {
        if (self::$doc === null) {
            $doc = json_decode(str_replace('"oneOf"', '"anyOf"', file_get_contents(__DIR__.'/../../../../spec/api/openapi.json')));
            $doc->components->schemas->WorkflowError->required = ['code', 'message'];
            foreach (['departments', 'portals', 'queues'] as $list) {
                $doc->components->schemas->WorkflowRegistry->properties->{$list}->type = ['array', 'null'];
            }
            self::$doc = $doc;
        }

        return self::$doc;
    }

    /** @return list<string> every operation of the spec, as "METHOD /path" */
    public static function operations(): array
    {
        $ops = [];
        foreach (get_object_vars(self::document()->paths) as $path => $item) {
            foreach (get_object_vars($item) as $method => $_) {
                $ops[] = strtoupper($method).' '.$path;
            }
        }

        return $ops;
    }

    /** Checks one API response against the spec and records which operation and status it covered. */
    public static function check(string $method, string $uri, TestResponse $response): void
    {
        $path = '/'.ltrim(parse_url($uri, PHP_URL_PATH), '/');
        $operation = self::operationFor($method, $path);
        Assert::assertNotNull($operation, "{$method} {$path} is not an operation of spec/api/openapi.json.");
        [$key, $op] = $operation;
        $status = (string) $response->getStatusCode();
        $body = $response->baseResponse instanceof StreamedResponse ? $response->streamedContent() : $response->getContent();
        $type = strtolower(trim(explode(';', (string) $response->headers->get('Content-Type'))[0]));
        $declared = $op->responses->{$status} ?? null;
        if ($declared !== null && ! isset($declared->content)) {
            Assert::assertSame('', $body, "{$key} {$status} documents no body.");
        } elseif ($declared !== null) {
            $schema = $declared->content->{$type}->schema ?? null;
            Assert::assertNotNull($schema, "{$key} {$status} does not document a {$type} body.");
            if ($type === 'application/json') {
                self::validate($body, $schema, "{$key} {$status}");
            }
        } else {
            Assert::assertGreaterThanOrEqual(400, (int) $status, "{$key} answered {$status}, which the spec does not document.");
            Assert::assertSame('application/json', $type, "{$key} {$status}: errors are JSON.");
            self::validate($body, (object) ['$ref' => '#/components/schemas/WorkflowError'], "{$key} {$status}");
        }
        self::$seen[$key][$status] = true;
    }

    /** @return array{0: string, 1: stdClass}|null the operation a request path matches (literal segments win over parameters) */
    public static function operationFor(string $method, string $path): ?array
    {
        $best = null;
        foreach (get_object_vars(self::document()->paths) as $template => $item) {
            $op = $item->{strtolower($method)} ?? null;
            $pattern = '#^'.preg_replace('#\\\\\{[^/]+\\\\\}#', '[^/]+', preg_quote($template, '#')).'$#';
            if ($op === null || ! preg_match($pattern, $path)) {
                continue;
            }
            $params = substr_count($template, '{');
            if ($best === null || $params < $best[2]) {
                $best = [strtoupper($method).' '.$template, $op, $params];
            }
        }

        return $best === null ? null : [$best[0], $best[1]];
    }

    private static function validate(string $body, stdClass $schema, string $what): void
    {
        if (self::$validator === null) {
            self::$validator = new Validator;
            self::$validator->resolver()->registerRaw(self::document(), self::BASE);
        }
        $schema = json_decode(str_replace('"#/components/', '"'.self::BASE.'#/components/', json_encode($schema, JSON_UNESCAPED_SLASHES)));
        $data = json_decode($body, false, 512, JSON_THROW_ON_ERROR);
        $result = self::$validator->validate($data, $schema);
        Assert::assertTrue($result->isValid(), "{$what} does not match the spec: ".json_encode(
            $result->isValid() ? [] : (new ErrorFormatter)->format($result->error()), JSON_PRETTY_PRINT | JSON_UNESCAPED_SLASHES)."\nBody: ".substr($body, 0, 2000));
    }
}
