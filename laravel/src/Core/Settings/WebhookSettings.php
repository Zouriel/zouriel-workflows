<?php

namespace Zouriel\Workflows\Core\Settings;

use stdClass;
use Zouriel\Workflows\Core\Json\Read;
use Zouriel\Workflows\Core\Json\Write;

/** Where webhooks may go (spec §12). Port of .NET `WebhookSettings`. */
final class WebhookSettings
{
    /** @var list<string>|null origins (`scheme://host[:port]`) a webhook `url` may use */
    public ?array $allowedOrigins = null;

    /** @var list<WebhookConnection>|null */
    public ?array $connections = null;

    public static function fromJson(stdClass $value, string $path): self
    {
        $webhooks = new self;
        $webhooks->allowedOrigins = Read::strings($value, 'allowedOrigins', $path);
        $webhooks->connections = Read::list($value, 'connections', $path, WebhookConnection::fromJson(...));

        return $webhooks;
    }

    public function toJson(): stdClass
    {
        return Write::object(['allowedOrigins' => $this->allowedOrigins, 'connections' => Write::list($this->connections)]);
    }
}
