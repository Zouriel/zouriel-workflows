<?php

namespace Zouriel\Workflows\Laravel\Adapters;

use Illuminate\Http\Client\ConnectionException;
use Illuminate\Http\Client\Factory;
use InvalidArgumentException;
use Zouriel\Workflows\Core\Host\WebhookClient;
use Zouriel\Workflows\Core\Host\WebhookFailedException;
use Zouriel\Workflows\Core\Host\WebhookRequest;
use Zouriel\Workflows\Core\Host\WebhookResponse;

/**
 * Sends webhooks with Laravel's HTTP client. Redirects are never followed (a 3xx is returned as the status, so it cannot
 * bounce the call to another host), responses are capped in size, and unless private networks are allowed the host is
 * resolved first, refused when it resolves only to private or local addresses, and the connection is pinned to an
 * address that passed the check (so DNS cannot change between check and connect). Port of .NET `HttpWebhookClient`.
 */
final class HttpWebhookClient implements WebhookClient
{
    /** @param  (callable(string): list<string>)|null  $resolve  host => addresses (default: the system resolver) */
    public function __construct(
        private readonly Factory $http,
        private readonly bool $allowPrivateNetworks = false,
        private readonly int $maxResponseBytes = 1048576,
        private readonly int $connectTimeoutSeconds = 10,
        private $resolve = null,
    ) {}

    public function send(WebhookRequest $request): WebhookResponse
    {
        $headers = ['Idempotency-Key' => $request->idempotencyKey];
        foreach ($request->headers as $name => $value) {
            if (! self::isValidHeader((string) $name, $value)) {
                throw new InvalidArgumentException("The webhook header '{$name}' has characters a header cannot hold.");
            }
            // Content headers belong to the body; without a body they are left out.
            if (stripos((string) $name, 'Content-') === 0 && ($request->body === null || $request->method === 'GET')) {
                continue;
            }
            $headers[$name] = $value;
        }
        $options = ['allow_redirects' => false, 'proxy' => '', 'connect_timeout' => $this->connectTimeoutSeconds, 'http_errors' => false];
        if (! $this->allowPrivateNetworks) {
            $options['curl'] = [CURLOPT_RESOLVE => [$this->pinnedAddress($request->url)]];
        }
        $pending = $this->http->withHeaders($headers)->withOptions($options)->timeout(max(1, (int) ceil(($request->timeoutMs ?? 30000) / 1000)));
        if ($request->body !== null && $request->method !== 'GET') {
            $pending = $pending->withBody($request->body, $headers['Content-Type'] ?? 'application/json');
        }
        try {
            $response = $pending->send($request->method, $request->url);
        } catch (ConnectionException $e) {
            throw new WebhookFailedException($e->getMessage(), 0, $e);
        }
        $body = $response->body();
        if (strlen($body) > $this->maxResponseBytes) {
            throw new WebhookFailedException("The webhook response is larger than {$this->maxResponseBytes} bytes.");
        }

        return new WebhookResponse($response->status(), $body === '' ? null : $body);
    }

    /** `host:port:address` for curl, with an address of the URL's host that is not private (refused when there is none). */
    private function pinnedAddress(string $url): string
    {
        $parts = parse_url($url);
        $host = trim((string) ($parts['host'] ?? ''), '[]');
        $port = $parts['port'] ?? (strtolower($parts['scheme'] ?? '') === 'https' ? 443 : 80);
        $addresses = filter_var($host, FILTER_VALIDATE_IP) !== false ? [$host] : ($this->resolve ?? self::resolve(...))($host);
        $allowed = array_values(array_filter($addresses, fn (string $a) => ! self::isPrivate($a)));
        if ($allowed === []) {
            throw new WebhookFailedException("Webhooks may not call '{$host}': it resolves to a private or local network address.");
        }
        $address = str_contains($allowed[0], ':') ? "[{$allowed[0]}]" : $allowed[0];

        return "{$host}:{$port}:{$address}";
    }

    /** @return list<string> */
    private static function resolve(string $host): array
    {
        $v4 = gethostbynamel($host) ?: [];
        $v6 = array_column(@dns_get_record($host, DNS_AAAA) ?: [], 'ipv6');

        return [...$v4, ...$v6];
    }

    /**
     * True for loopback, private, link-local, carrier-grade NAT, unique-local, multicast, reserved, documentation and
     * unspecified addresses, and IPv6 forms carrying such an IPv4 address (mapped, compatible, NAT64, 6to4); Teredo is
     * always refused.
     */
    public static function isPrivate(string $address): bool
    {
        $bytes = @inet_pton($address);
        if ($bytes === false) {
            return true;
        }
        $b = array_values(unpack('C*', $bytes));
        if (count($b) === 16) {
            $zeros = fn (int $from, int $to) => array_sum(array_slice($b, $from, $to - $from)) === 0;
            $v4 = match (true) {
                $zeros(0, 10) && (($b[10] === 0 && $b[11] === 0 && ($b[12] | $b[13] | $b[14]) !== 0) || ($b[10] === 0xFF && $b[11] === 0xFF)) => array_slice($b, 12, 4),
                $zeros(0, 8) && $b[8] === 0xFF && $b[9] === 0xFF && $b[10] === 0 && $b[11] === 0 => array_slice($b, 12, 4),
                $b[0] === 0 && $b[1] === 0x64 && $b[2] === 0xFF && $b[3] === 0x9B && ($zeros(4, 12) || ($b[4] === 0 && $b[5] === 1)) => array_slice($b, 12, 4),
                $b[0] === 0x20 && $b[1] === 0x02 => array_slice($b, 2, 4),
                default => null,
            };
            if ($v4 !== null) {
                return self::isPrivate(implode('.', $v4));
            }

            return ($b[0] === 0x20 && $b[1] === 0x01 && $b[2] === 0 && $b[3] === 0)
                || $zeros(0, 15) // :: and ::1
                || $b[0] === 0xFF // multicast
                || ($b[0] === 0xFE && ($b[1] & 0xC0) === 0x80) // link-local
                || ($b[0] === 0xFE && ($b[1] & 0xC0) === 0xC0) // site-local
                || ($b[0] & 0xFE) === 0xFC // unique-local
                || ($b[0] === 0x20 && $b[1] === 0x01 && $b[2] === 0x0D && $b[3] === 0xB8); // documentation
        }

        return match (true) {
            in_array($b[0], [0, 10, 127], true) => true,
            $b[0] === 100 => $b[1] >= 64 && $b[1] <= 127,
            $b[0] === 169 => $b[1] === 254,
            $b[0] === 172 => $b[1] >= 16 && $b[1] <= 31,
            $b[0] === 192 => $b[1] === 168 || ($b[1] === 0 && in_array($b[2], [0, 2], true)),
            $b[0] === 198 => in_array($b[1], [18, 19], true) || ($b[1] === 51 && $b[2] === 100),
            $b[0] === 203 => $b[1] === 0 && $b[2] === 113,
            default => $b[0] >= 224,
        };
    }

    /** A header name that is an HTTP token and a value without control characters (CR/LF would start another header). */
    public static function isValidHeader(string $name, string $value): bool
    {
        return preg_match('/^[!#$%&\'*+\-.^_`|~0-9A-Za-z]+$/', $name) === 1 && preg_match('/[\x00-\x08\x0A-\x1F\x7F]/', $value) !== 1;
    }
}
