<?php

namespace Zouriel\Workflows\Core\Support;

/**
 * An absolute URL read the way .NET's `Uri.TryCreate(text, UriKind.Absolute, …)` reads the ones
 * settings and webhooks use: scheme and host lower-cased, the scheme's default port left out of the
 * authority. Used where the reference compares origins (`Lint/Linter.cs`, `Settings/GlobalSettings.cs`).
 */
final readonly class AbsoluteUrl
{
    private const array DEFAULT_PORTS = ['http' => 80, 'https' => 443, 'ftp' => 21, 'ws' => 80, 'wss' => 443];

    private function __construct(
        public string $scheme,
        public string $host,
        public ?int $port,
        public ?string $userInfo,
    ) {}

    public static function tryParse(?string $text): ?self
    {
        if ($text === null || $text === '' || ! preg_match('/^([A-Za-z][A-Za-z0-9+.-]*):\/\/([^\/?#]*)/', $text, $m)) {
            return null;
        }
        $authority = $m[2];
        $userInfo = null;
        if (($at = strrpos($authority, '@')) !== false) {
            $userInfo = substr($authority, 0, $at);
            $authority = substr($authority, $at + 1);
        }
        if (! preg_match('/^(\[[0-9A-Fa-f:.]+\]|[A-Za-z0-9._~%!$&\'()*+,;=-]+)(?::([0-9]{1,5}))?$/', $authority, $h)) {
            return null;
        }
        $port = isset($h[2]) && $h[2] !== '' ? (int) $h[2] : null;
        if ($port !== null && $port > 65535) {
            return null;
        }
        $scheme = strtolower($m[1]);
        if ($port === (self::DEFAULT_PORTS[$scheme] ?? -1)) {
            $port = null;
        }

        return new self($scheme, strtolower($h[1]), $port, $userInfo);
    }

    public function isHttp(): bool
    {
        return $this->scheme === 'http' || $this->scheme === 'https';
    }

    /** Host and port (when not the scheme's default), without user info (.NET `Uri.Authority`). */
    public function authority(): string
    {
        return $this->host.($this->port === null ? '' : ":{$this->port}");
    }

    /** `scheme://authority`, the origin a webhook URL is checked against. */
    public function origin(): string
    {
        return "{$this->scheme}://{$this->authority()}";
    }

    /** `scheme://[userinfo@]authority` (.NET `Uri.GetLeftPart(UriPartial.Authority)`). */
    public function leftPart(): string
    {
        return "{$this->scheme}://".($this->userInfo === null ? '' : "{$this->userInfo}@").$this->authority();
    }
}
