<?php

use Zouriel\Workflows\Core\Time\Cron;
use Zouriel\Workflows\Core\Time\IsoDuration;
use Zouriel\Workflows\Core\WorkflowException;

// Expectations from .NET CoreUnitTests (Durations_parse_strictly, Cron_rejects_bad_schedules) and
// EngineBehaviourTests (Durations_over_fifty_years_are_invalid).

it('parses durations strictly', function (string $text, bool $ok) {
    expect(IsoDuration::tryParse($text) !== null)->toBe($ok);
})->with([
    ['P3D', true], ['PT0S', true], ['-P1D', true], ['P', false], ['3 days', false], ['PT', false],
    ['P50Y', true], ['P51Y', false], ['PT99999999999999999999H', false], ['P1DT', false],
]);

it('keeps the parts and the sign', function () {
    $d = IsoDuration::tryParse('-P1Y2M3W4DT5H6M7S');

    expect([$d->negative, $d->years, $d->months, $d->weeks, $d->days, $d->hours, $d->minutes, $d->seconds])->toBe([true, 1, 2, 3, 4, 5, 6, 7])
        ->and($d->totalDays())->toBe(25)
        ->and(IsoDuration::tryParse('P1M')->approximateSeconds())->toBe(30 * 86400)
        ->and(IsoDuration::tryParse('PT0S')->isZero())->toBeTrue();
});

it('refuses a duration over fifty years with badDuration', function () {
    try {
        IsoDuration::parse('P999999Y');
        $this->fail('no exception');
    } catch (WorkflowException $e) {
        expect($e->errorCode)->toBe('badDuration');
    }
});

it('rejects bad schedules', function (string $expression, string $message) {
    [$cron, $error] = Cron::tryParse($expression);

    expect($cron)->toBeNull()->and($error)->toBe($message);
})->with([
    ['0 8 * *', 'A schedule needs 5 parts: minute hour day month weekday.'],
    ['61 * * * *', "'61' must be a number from 0 to 59."],
    ['0 8 * * 9', "'9' must be a number from 0 to 7."],
    ['5-1 * * * *', "'5-1' is a backwards range."],
    ['*/0 * * * *', "'0' must be a number from 1 to 59."],
]);

it('reads lists, ranges, steps and Sunday as 0 or 7', function () {
    [$cron] = Cron::tryParse('0,30 9-17/4 * * 7');

    expect(array_keys($cron->minutes))->toBe([0, 30])
        ->and(array_keys($cron->hours))->toBe([9, 13, 17])
        ->and($cron->daysOfWeek)->toBe([0 => true])
        ->and($cron->anyDayOfMonth)->toBeTrue()
        ->and($cron->anyDayOfWeek)->toBeFalse();
});
