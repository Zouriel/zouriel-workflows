<?php

namespace Zouriel\Workflows\Core\Model;

use stdClass;
use Zouriel\Workflows\Core\Json\Read;

/** Calls an HTTP endpoint on a named connection or an allowed origin (outputs `status`, `body`). Port of .NET `WebhookStep`. */
final class WebhookStep extends Step
{
    /** The HTTP methods a webhook may use, in capitals as sent (the schema's `method` enum). */
    public const array METHODS = ['GET', 'POST', 'PUT', 'PATCH', 'DELETE'];

    /** A connection key from settings; null when `url` is used. */
    public ?string $connection = null;

    /** An absolute URL whose origin must be allowed in settings. */
    public ?string $url = null;

    public string $method = 'POST';

    /** Appended to the connection or URL. */
    public ?Template $path = null;

    /** @var array<string, Template>|null */
    public ?array $headers = null;

    /** JSON body; `{"ref": path}` objects are replaced by their values. */
    public mixed $body = null;

    /** @var list<int>|null statuses that count as success; null = any 2xx */
    public ?array $expect = null;

    protected function read(stdClass $value, string $path): void
    {
        $this->connection = Read::optionalString($value, 'connection', $path);
        $this->url = Read::optionalString($value, 'url', $path);
        $this->method = Read::string($value, 'method', $path, 'POST');
        $this->path = Template::fromJson(Read::value($value, 'path'), "{$path}.path");
        $this->headers = Read::map($value, 'headers', $path, fn ($v, $p) => Template::fromJson($v, $p) ?? new Template);
        $this->body = Read::value($value, 'body');
        $this->expect = Read::list($value, 'expect', $path, Read::toInt(...));
    }

    protected function members(): array
    {
        return [
            'connection' => $this->connection, 'url' => $this->url, 'method' => $this->method, 'path' => $this->path?->toJson(),
            'headers' => $this->headers === null ? null : (object) array_map(fn (Template $t) => $t->toJson(), $this->headers),
            'body' => $this->body, 'expect' => $this->expect,
        ];
    }
}
