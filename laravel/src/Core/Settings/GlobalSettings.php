<?php

namespace Zouriel\Workflows\Core\Settings;

use DateTimeZone;
use JsonException;
use stdClass;
use Zouriel\Forms\Js;
use Zouriel\Forms\Json;
use Zouriel\Workflows\Core\Json\Read;
use Zouriel\Workflows\Core\Json\Write;
use Zouriel\Workflows\Core\Support\AbsoluteUrl;
use Zouriel\Workflows\Core\Time\IsoDuration;

/**
 * App-wide workflow settings (spec §12), edited in `ui-workflow-global-settings`. Port of .NET
 * `Settings/GlobalSettings.cs`: `parse()` reads them, `problems()` lists what saving refuses, and the
 * engine reads values it cannot use leniently (the work calendar comes with the engine).
 */
final class GlobalSettings
{
    private const array DAYS = ['mon', 'tue', 'wed', 'thu', 'fri', 'sat', 'sun'];

    public int $version = 1;

    /** IANA time zone id for calendars, date triggers and schedules; null = UTC. */
    public ?string $timeZone = null;

    public ?CalendarSettings $calendar = null;

    public ?DefaultSettings $defaults = null;

    public ?PeopleSettings $people = null;

    /** @var array<string, ChannelSettings>|null per notification channel (by registry key) */
    public ?array $channels = null;

    public ?WebhookSettings $webhooks = null;

    public ?AuditSettings $audit = null;

    /** @var list<PausedEntity>|null entities whose triggers are paused */
    public ?array $paused = null;

    /**
     * Reads settings JSON; null or blank gives the defaults.
     *
     * @throws JsonException
     */
    public static function parse(string|stdClass|null $json): self
    {
        if ($json === null || (is_string($json) && Js::trim($json) === '')) {
            return new self;
        }
        $value = is_string($json) ? Json::decode($json) : $json;
        if ($value === null) {
            return new self;
        }
        $value = Read::itemObject($value, '$', 'Zouriel.Workflows.Settings.GlobalSettings');
        $settings = new self;
        $settings->version = Read::int($value, 'version', '$', 1);
        $settings->timeZone = Read::optionalString($value, 'timeZone', '$');
        $settings->calendar = Read::model($value, 'calendar', '$', 'Zouriel.Workflows.Settings.CalendarSettings', CalendarSettings::fromJson(...));
        $settings->defaults = Read::model($value, 'defaults', '$', 'Zouriel.Workflows.Settings.DefaultSettings', DefaultSettings::fromJson(...));
        $settings->people = Read::model($value, 'people', '$', 'Zouriel.Workflows.Settings.PeopleSettings', PeopleSettings::fromJson(...));
        $settings->channels = Read::map($value, 'channels', '$', ChannelSettings::fromJson(...));
        $settings->webhooks = Read::model($value, 'webhooks', '$', 'Zouriel.Workflows.Settings.WebhookSettings', WebhookSettings::fromJson(...));
        $settings->audit = Read::model($value, 'audit', '$', 'Zouriel.Workflows.Settings.AuditSettings', AuditSettings::fromJson(...));
        $settings->paused = Read::list($value, 'paused', '$', PausedEntity::fromJson(...));

        return $settings;
    }

    /** The settings as JSON (what `GET /settings` serves): null members left out, unknown ones dropped. */
    public function toJson(): stdClass
    {
        return Write::object([
            'version' => $this->version, 'timeZone' => $this->timeZone, 'calendar' => $this->calendar?->toJson(),
            'defaults' => $this->defaults?->toJson(), 'people' => $this->people?->toJson(), 'channels' => Write::map($this->channels),
            'webhooks' => $this->webhooks?->toJson(), 'audit' => $this->audit?->toJson(), 'paused' => Write::list($this->paused),
        ]);
    }

    /** The loop protection limit (spec §5), default 5. */
    public function maxChainDepth(): int
    {
        return $this->defaults?->maxChainDepth ?? 5;
    }

    /** The most items a `forEach` covers, default 500. */
    public function forEachMax(): int
    {
        return $this->defaults?->forEachMax ?? 500;
    }

    /** The concurrency mode for automations that set none, default `perRecordSerial`. */
    public function defaultConcurrency(): string
    {
        return $this->defaults?->concurrency ?? 'perRecordSerial';
    }

    public function isPaused(?string $entity): bool
    {
        return $entity !== null && array_any($this->paused ?? [], fn (PausedEntity $p) => $p->entity === $entity);
    }

    /** The configured time zone, or UTC when unset or unknown. */
    public function zone(): DateTimeZone
    {
        return self::knownZone($this->timeZone) ? new DateTimeZone($this->timeZone) : new DateTimeZone('UTC');
    }

    /**
     * What is wrong with these settings, in plain words (empty when they are fine): every field checked
     * against `schema/settings.schema.json` and what the engine can use (spec §12). Saving refuses
     * settings with problems (`invalidSettings`).
     *
     * @return list<string>
     */
    public function problems(): array
    {
        $p = [];
        if ($this->timeZone !== null && Js::trim($this->timeZone) !== '' && ! self::knownZone($this->timeZone)) {
            $p[] = "Unknown time zone '{$this->timeZone}'.";
        }
        if ($this->calendar !== null) {
            $calendar = $this->calendar;
            foreach ($calendar->days ?? [] as $day) {
                if (! in_array($day, self::DAYS, true)) {
                    $p[] = "'{$day}' is not a day; use mon, tue, wed, thu, fri, sat or sun.";
                }
            }
            if ($calendar->days !== null && count(array_unique($calendar->days)) !== count($calendar->days)) {
                $p[] = 'A working day is listed twice.';
            }
            if ($calendar->hours !== null) {
                if (count($calendar->hours) !== 2) {
                    $p[] = 'Working hours are a start and an end time.';
                } elseif (! self::isTime($calendar->hours[0]) || ! self::isTime($calendar->hours[1])) {
                    $p[] = 'Working hours must be times like 09:00 and 17:00 (HH:mm).';
                } elseif ($calendar->hours[1] <= $calendar->hours[0]) {
                    $p[] = 'Working hours must end after they start.';
                }
            }
            foreach ($calendar->holidays ?? [] as $holiday) {
                if (! self::isDate($holiday)) {
                    $p[] = "'{$holiday}' is not a date (YYYY-MM-DD).";
                }
            }
        }
        if ($this->defaults !== null) {
            $defaults = $this->defaults;
            if ($defaults->retry !== null) {
                $retry = $defaults->retry;
                if ($retry->max < 0 || $retry->max > 10) {
                    $p[] = 'Retries can be 0 to 10.';
                }
                if (! in_array($retry->backoff, [null, 'fixed', 'exponential'], true)) {
                    $p[] = "'{$retry->backoff}' is not a way to space retries (fixed or exponential).";
                }
                foreach ([$retry->initial, $retry->maxDelay] as $duration) {
                    if ($duration !== null && IsoDuration::tryParse($duration) === null) {
                        $p[] = "'{$duration}' is not a duration.";
                    }
                }
            }
            foreach ($defaults->reminders ?? [] as $reminder) {
                if (IsoDuration::tryParse($reminder)?->negative ?? true) {
                    $p[] = "'{$reminder}' is not a duration.";
                }
            }
            if (! in_array($defaults->concurrency, [null, 'perRecordSerial', 'parallel', 'skipIfRunning'], true)) {
                $p[] = "'{$defaults->concurrency}' is not a concurrency mode (perRecordSerial, parallel or skipIfRunning).";
            }
            if ($defaults->maxChainDepth !== null && ($defaults->maxChainDepth < 1 || $defaults->maxChainDepth > 20)) {
                $p[] = 'The loop protection limit (maxChainDepth) must be 1 to 20.';
            }
            if ($defaults->forEachMax !== null && ($defaults->forEachMax < 1 || $defaults->forEachMax > 500)) {
                $p[] = "The most items a 'for each' covers (forEachMax) must be 1 to 500.";
            }
        }
        foreach ($this->webhooks?->allowedOrigins ?? [] as $origin) {
            $url = AbsoluteUrl::tryParse($origin);
            if ($url === null || ! $url->isHttp() || $url->leftPart() !== rtrim($origin, '/') || str_ends_with($origin, '/')) {
                $p[] = "'{$origin}' is not an origin like https://api.example.com (scheme and host only).";
            }
        }
        $keys = [];
        foreach ($this->webhooks?->connections ?? [] as $connection) {
            if (Js::trim($connection->key) === '' || ! preg_match('/^[a-z][a-zA-Z0-9_]*$/', $connection->key)) {
                $p[] = "Connection key '{$connection->key}' must start with a lower-case letter and use letters, digits and _.";
            } elseif (isset($keys[$connection->key])) {
                $p[] = "Two connections are called '{$connection->key}'.";
            } else {
                $keys[$connection->key] = true;
            }
            if (! (AbsoluteUrl::tryParse($connection->baseUrl)?->isHttp() ?? false)) {
                $p[] = "Connection '{$connection->key}' needs an http(s) address.";
            }
            if (! in_array($connection->auth, [null, 'none', 'secret'], true)) {
                $p[] = "Connection '{$connection->key}': '{$connection->auth}' is not an auth kind (none or secret).";
            }
            if ($connection->auth === 'secret' && Js::trim($connection->secretName ?? '') === '') {
                $p[] = "Connection '{$connection->key}' uses a secret but names none.";
            }
            if ($connection->header !== null && ($connection->header === '' || preg_match('/[\x00-\x20\x7F-\xFF()<>@,;:\\\\"\/\[\]?={}]/', $connection->header))) {
                $p[] = "Connection '{$connection->key}': '{$connection->header}' is not a header name.";
            }
        }
        foreach ($this->audit?->retentionDays ?? [] as $category => $days) {
            if ($days < 1) {
                $p[] = "Audit entries of '{$category}' must be kept at least 1 day.";
            }
        }
        foreach ($this->audit?->maskedFields ?? [] as $masked) {
            $dot = strpos($masked, '.');
            if ($dot === false || $dot === 0 || $dot === strlen($masked) - 1) {
                $p[] = "'{$masked}' is not a masked field like entity.field or input.key.";
            }
        }
        foreach ($this->paused ?? [] as $paused) {
            if (Js::trim($paused->entity) === '') {
                $p[] = 'A paused entry names no record type.';
            }
        }

        return $p;
    }

    /** Whether the system knows the zone id exactly as written (as .NET finds IANA zones on Linux). */
    private static function knownZone(?string $id): bool
    {
        return $id !== null && in_array($id, DateTimeZone::listIdentifiers(DateTimeZone::ALL_WITH_BC), true);
    }

    /** `HH:mm`, as .NET `TimeOnly.TryParseExact(s, "HH:mm")`. */
    private static function isTime(?string $text): bool
    {
        return $text !== null && preg_match('/^([01][0-9]|2[0-3]):[0-5][0-9]$/', $text) === 1;
    }

    /** A real date `yyyy-MM-dd`, as .NET `DateOnly.TryParseExact`. */
    private static function isDate(?string $text): bool
    {
        return $text !== null && preg_match('/^([0-9]{4})-([0-9]{2})-([0-9]{2})$/', $text, $m) === 1 && checkdate((int) $m[2], (int) $m[3], (int) $m[1]);
    }
}
