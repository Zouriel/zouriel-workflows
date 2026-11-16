<?php

use Zouriel\Forms\Json;
use Zouriel\Workflows\Core\Settings\GlobalSettings;

// Expectations from .NET Settings/GlobalSettings.cs and AuditFixTests (Settings_with_a_bad_value_are_refused_on_save,
// Good_settings_still_save).

it('finds a problem in each bad value', function (string $json, string $problem) {
    expect(GlobalSettings::parse($json)->problems())->toBe([$problem]);
})->with([
    ['{ "version": 1, "calendar": { "hours": ["09:00", "5pm"] } }', 'Working hours must be times like 09:00 and 17:00 (HH:mm).'],
    ['{ "version": 1, "calendar": { "hours": ["09:00"] } }', 'Working hours are a start and an end time.'],
    ['{ "version": 1, "calendar": { "hours": ["17:00", "09:00"] } }', 'Working hours must end after they start.'],
    ['{ "version": 1, "calendar": { "holidays": ["2026-02-30"] } }', "'2026-02-30' is not a date (YYYY-MM-DD)."],
    ['{ "version": 1, "calendar": { "days": ["monday"] } }', "'monday' is not a day; use mon, tue, wed, thu, fri, sat or sun."],
    ['{ "version": 1, "calendar": { "days": ["mon", "mon"] } }', 'A working day is listed twice.'],
    ['{ "version": 1, "defaults": { "maxChainDepth": 0 } }', 'The loop protection limit (maxChainDepth) must be 1 to 20.'],
    ['{ "version": 1, "defaults": { "maxChainDepth": 50 } }', 'The loop protection limit (maxChainDepth) must be 1 to 20.'],
    ['{ "version": 1, "defaults": { "forEachMax": 501 } }', "The most items a 'for each' covers (forEachMax) must be 1 to 500."],
    ['{ "version": 1, "defaults": { "concurrency": "whenever" } }', "'whenever' is not a concurrency mode (perRecordSerial, parallel or skipIfRunning)."],
    ['{ "version": 1, "defaults": { "reminders": ["tomorrow"] } }', "'tomorrow' is not a duration."],
    ['{ "version": 1, "defaults": { "reminders": ["-P1D"] } }', "'-P1D' is not a duration."],
    ['{ "version": 1, "defaults": { "retry": { "max": 11 } } }', 'Retries can be 0 to 10.'],
    ['{ "version": 1, "defaults": { "retry": { "backoff": "linear" } } }', "'linear' is not a way to space retries (fixed or exponential)."],
    ['{ "version": 1, "audit": { "retentionDays": { "step": 0 } } }', "Audit entries of 'step' must be kept at least 1 day."],
    ['{ "version": 1, "audit": { "maskedFields": ["applicantEmail"] } }', "'applicantEmail' is not a masked field like entity.field or input.key."],
    ['{ "version": 1, "webhooks": { "allowedOrigins": ["https://api.example.com/path"] } }', "'https://api.example.com/path' is not an origin like https://api.example.com (scheme and host only)."],
    ['{ "version": 1, "webhooks": { "allowedOrigins": ["https://API.example.com"] } }', "'https://API.example.com' is not an origin like https://api.example.com (scheme and host only)."],
    ['{ "version": 1, "webhooks": { "allowedOrigins": ["https://api.example.com:443"] } }', "'https://api.example.com:443' is not an origin like https://api.example.com (scheme and host only)."],
    ['{ "version": 1, "webhooks": { "connections": [ { "key": "crm", "label": "CRM", "baseUrl": "https://crm.example.com", "auth": "secret" } ] } }', "Connection 'crm' uses a secret but names none."],
    ['{ "version": 1, "webhooks": { "connections": [ { "key": "crm", "label": "CRM", "baseUrl": "https://crm.example.com", "header": "X Bad" } ] } }', "Connection 'crm': 'X Bad' is not a header name."],
    ['{ "version": 1, "webhooks": { "connections": [ { "key": "Crm", "label": "CRM", "baseUrl": "https://crm.example.com" } ] } }', "Connection key 'Crm' must start with a lower-case letter and use letters, digits and _."],
    ['{ "version": 1, "webhooks": { "connections": [ { "key": "crm", "label": "CRM", "baseUrl": "ftp://crm.example.com" } ] } }', "Connection 'crm' needs an http(s) address."],
    ['{ "version": 1, "timeZone": "Mars/Olympus" }', "Unknown time zone 'Mars/Olympus'."],
    ['{ "version": 1, "paused": [ { "entity": " " } ] }', 'A paused entry names no record type.'],
]);

it('accepts good settings', function () {
    $json = '{ "version": 1, "timeZone": "Europe/London", "calendar": { "days": ["mon", "tue"], "hours": ["08:30", "16:00"], "holidays": ["2026-12-25"] },
      "defaults": { "retry": { "max": 3, "backoff": "exponential", "initial": "PT1M" }, "reminders": ["P1D"], "concurrency": "parallel", "maxChainDepth": 7, "forEachMax": 100 },
      "webhooks": { "allowedOrigins": ["https://api.example.com", "http://localhost:8080"], "connections": [ { "key": "crm", "label": "CRM", "baseUrl": "https://crm.example.com/v1", "auth": "secret", "secretName": "crm", "header": "X-Api-Key" } ] },
      "audit": { "retentionDays": { "step": 30 }, "maskedFields": ["application.notes", "input.reason"] }, "paused": [ { "entity": "document" } ] }';
    $settings = GlobalSettings::parse($json);

    expect($settings->problems())->toBe([])
        ->and([$settings->maxChainDepth(), $settings->forEachMax(), $settings->defaultConcurrency()])->toBe([7, 100, 'parallel'])
        ->and($settings->isPaused('document'))->toBeTrue()
        ->and($settings->zone()->getName())->toBe('Europe/London');
});

it('reads blank settings as the defaults and writes back only what it knows', function () {
    $defaults = GlobalSettings::parse('  ');

    expect([$defaults->maxChainDepth(), $defaults->forEachMax(), $defaults->defaultConcurrency(), $defaults->zone()->getName()])->toBe([5, 500, 'perRecordSerial', 'UTC'])
        ->and(Json::encode(GlobalSettings::parse('{ "version": 1, "extra": true, "channels": { "email": { "enabled": false } } }')->toJson()))
        ->toBe('{"version":1,"channels":{"email":{"enabled":false}}}');
});
