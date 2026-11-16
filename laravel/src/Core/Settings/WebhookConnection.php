<?php

namespace Zouriel\Workflows\Core\Settings;

use stdClass;
use Zouriel\Workflows\Core\Json\Read;
use Zouriel\Workflows\Core\Json\Write;

/** A named webhook target whose secret stays with the host. Port of .NET `WebhookConnection`. */
final class WebhookConnection
{
    public string $key = '';

    public string $label = '';

    /** The base address; step paths are appended and must stay on its host. */
    public string $baseUrl = '';

    /** `none` or `secret`. */
    public ?string $auth = null;

    /** The secret the host resolves, for `secret` auth. */
    public ?string $secretName = null;

    /** The header carrying the secret as is; null = `Authorization: Bearer`. */
    public ?string $header = null;

    public static function fromJson(mixed $value, string $path): self
    {
        $value = Read::itemObject($value, $path, 'Zouriel.Workflows.Settings.WebhookConnection');
        $connection = new self;
        $connection->key = Read::string($value, 'key', $path);
        $connection->label = Read::string($value, 'label', $path);
        $connection->baseUrl = Read::string($value, 'baseUrl', $path);
        $connection->auth = Read::optionalString($value, 'auth', $path);
        $connection->secretName = Read::optionalString($value, 'secretName', $path);
        $connection->header = Read::optionalString($value, 'header', $path);

        return $connection;
    }

    public function toJson(): stdClass
    {
        return Write::object([
            'key' => $this->key, 'label' => $this->label, 'baseUrl' => $this->baseUrl, 'auth' => $this->auth,
            'secretName' => $this->secretName, 'header' => $this->header,
        ]);
    }
}
