<?php

use Zouriel\Workflows\Core\Time\Cron;
use Zouriel\Workflows\Core\Time\Instant;
use Zouriel\Workflows\Core\Time\IsoDuration;
use Zouriel\Workflows\Core\Time\WorkCalendar;

// The work calendar, Cron::next and instants, with the cases of .NET CoreUnitTests and SlaPauseCalendarTests:
// Monday to Friday 09:00-17:00 UTC, Tuesday 2026-10-27 a holiday.

function workWeek(): WorkCalendar
{
    return new WorkCalendar(new DateTimeZone('UTC'), holidays: ['2026-10-27']);
}

function instantOf(string $text): int
{
    return Instant::parse($text);
}

it('writes and reads instants with milliseconds', function () {
    expect(Instant::format(instantOf('2026-10-19T09:00:00.123Z')))->toBe('2026-10-19T09:00:00.123Z')
        ->and(Instant::format(instantOf('2026-10-19T11:00:00+02:00')))->toBe('2026-10-19T09:00:00.000Z')
        ->and(Instant::format(instantOf('2026-10-19')))->toBe('2026-10-19T00:00:00.000Z')
        ->and(Instant::format(Instant::MAX))->toBe('9999-12-31T23:59:59.999Z')
        ->and(Instant::parse('next tuesday'))->toBeNull()
        ->and(Instant::parse('2026-02-30'))->toBeNull();
});

it('adds working time skipping nights, weekends and holidays', function (string $from, string $add, string $expected) {
    expect(Instant::format(workWeek()->add(instantOf($from), IsoDuration::parse($add), working: true)))->toBe(Instant::format(instantOf($expected)));
})->with([
    'Friday afternoon to Monday' => ['2026-10-23T16:00:00Z', 'PT2H', '2026-10-26T10:00:00Z'],
    'before hours' => ['2026-10-26T08:00:00Z', 'PT1H', '2026-10-26T10:00:00Z'],
    'skips the holiday on Tuesday' => ['2026-10-26T10:00:00Z', 'P1D', '2026-10-28T10:00:00Z'],
    'Saturday to Monday 09:00, a day past the holiday, an hour' => ['2026-10-24T12:00:00Z', 'P1DT1H', '2026-10-28T10:00:00Z'],
]);

it('keeps the local time of calendar days across daylight saving', function () {
    $london = new WorkCalendar(new DateTimeZone('Europe/London'), hasWorkingTime: false);

    // 09:00 BST on Friday + 3 days is 09:00 GMT on Monday (the clocks went back on Sunday 25 October).
    expect(Instant::format($london->add(instantOf('2026-10-23T08:00:00Z'), IsoDuration::parse('P3D'))))->toBe('2026-10-26T09:00:00.000Z')
        // A local time inside the spring gap moves forward to the first valid minute (01:30 does not exist on 29 March).
        ->and(Instant::format($london->atLocal('2026-03-29', 90)))->toBe('2026-03-29T01:00:00.000Z')
        // A local time the autumn change repeats (01:30 on 25 October) reads as standard time, as .NET does.
        ->and(Instant::format($london->atLocal('2026-10-25', 90)))->toBe('2026-10-25T01:30:00.000Z');
});

it('adds months by calendar, keeping the day or moving back to the month end', function () {
    $utc = WorkCalendar::utc();

    expect(Instant::format($utc->add(instantOf('2026-01-31T10:00:00Z'), IsoDuration::parse('P1M'))))->toBe('2026-02-28T10:00:00.000Z')
        ->and(Instant::format($utc->add(instantOf('2028-02-29T10:00:00Z'), IsoDuration::parse('P1Y1M'))))->toBe('2029-03-28T10:00:00.000Z')
        ->and(Instant::format($utc->add(instantOf('2026-03-31T10:00:00Z'), IsoDuration::parse('-P1M'))))->toBe('2026-02-28T10:00:00.000Z');
});

it('gives "never" for a time beyond the calendar instead of failing', function () {
    expect(WorkCalendar::utc()->add(instantOf('9999-12-31T00:00:00Z'), IsoDuration::parse('P2D')))->toBe(Instant::MAX)
        ->and(workWeek()->add(instantOf('9999-12-30T10:00:00Z'), IsoDuration::parse('P5D'), working: true))->toBe(Instant::MAX);
});

it('measures working time between two instants', function (string $from, string $to, float $hours) {
    expect(workWeek()->workingTimeBetween(instantOf($from), instantOf($to)))->toBe((int) ($hours * 3600000));
})->with([
    'inside one day' => ['2026-10-22T10:00:00Z', '2026-10-22T15:30:00Z', 5.5],
    'across a night' => ['2026-10-22T16:00:00Z', '2026-10-23T10:00:00Z', 2],
    'across a weekend' => ['2026-10-23T16:00:00Z', '2026-10-26T10:00:00Z', 2],
    'across a holiday' => ['2026-10-26T16:00:00Z', '2026-10-28T10:00:00Z', 2],
    'only a weekend' => ['2026-10-24T10:00:00Z', '2026-10-25T10:00:00Z', 0],
    'Thursday to Monday' => ['2026-10-22T10:00:00Z', '2026-10-26T10:00:00Z', 16],
]);

it('measures plain time without a calendar and nothing backwards', function () {
    expect(workWeek()->workingTimeBetween(instantOf('2026-10-22T12:00:00Z'), instantOf('2026-10-22T11:00:00Z')))->toBe(0)
        ->and(WorkCalendar::utc()->workingTimeBetween(instantOf('2026-10-22T12:00:00Z'), instantOf('2026-10-23T18:00:00Z')))->toBe(30 * 3600000)
        ->and(workWeek()->workingTimeBetween(instantOf('2026-10-22T12:00:00Z'), instantOf('2026-10-23T18:00:00Z'), working: false))->toBe(30 * 3600000);
});

it('adds exact working time, the inverse of measuring it', function () {
    $hour = 3600000;

    expect(Instant::format(workWeek()->addTime(instantOf('2026-10-22T16:00:00Z'), 10 * $hour, true)))->toBe('2026-10-26T10:00:00.000Z')
        ->and(Instant::format(workWeek()->addTime(instantOf('2026-10-24T16:00:00Z'), 10 * $hour, true)))->toBe('2026-10-28T11:00:00.000Z')
        ->and(Instant::format(workWeek()->addTime(instantOf('2026-10-22T16:00:00Z'), 10 * $hour)))->toBe('2026-10-23T02:00:00.000Z')
        ->and(workWeek()->addTime(instantOf('2026-10-22T10:00:00Z'), 0, true))->toBe(instantOf('2026-10-22T10:00:00Z'));
    foreach ([['2026-10-22T10:00:00Z', 3], ['2026-10-23T16:30:00Z', 9], ['2026-10-24T03:00:00Z', 17], ['2026-10-26T16:59:00Z', 40]] as [$from, $hours]) {
        expect(workWeek()->workingTimeBetween(instantOf($from), workWeek()->addTime(instantOf($from), $hours * $hour, true)))->toBe($hours * $hour);
    }
});

it('finds the next run of a schedule', function (string $expression, string $after, string $expected) {
    [$cron] = Cron::tryParse($expression);

    expect(Instant::format($cron->next(instantOf($after), WorkCalendar::utc())))->toBe(Instant::format(instantOf($expected)));
})->with([
    ['0 8 * * 1-5', '2026-10-23T08:30:00Z', '2026-10-26T08:00:00Z'],
    ['*/15 * * * *', '2026-10-23T08:31:00Z', '2026-10-23T08:45:00Z'],
    ['0 0 1 * *', '2026-10-23T08:30:00Z', '2026-11-01T00:00:00Z'],
    ['30 9 * * 0', '2026-10-23T08:30:00Z', '2026-10-25T09:30:00Z'],
]);

it('runs schedules in the calendar time zone and never on a date that does not come', function () {
    [$daily] = Cron::tryParse('0 9 * * *');
    [$never] = Cron::tryParse('0 0 30 2 *');
    $male = new WorkCalendar(new DateTimeZone('Indian/Maldives'), hasWorkingTime: false);

    expect(Instant::format($daily->next(instantOf('2026-10-23T05:00:00Z'), $male)))->toBe('2026-10-24T04:00:00.000Z')
        ->and($never->next(instantOf('2026-10-23T05:00:00Z'), WorkCalendar::utc()))->toBeNull();
});
