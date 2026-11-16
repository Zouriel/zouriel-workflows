using Zouriel.Workflows.Engine;
using Zouriel.Workflows.Model;
using Zouriel.Workflows.Storage;
using Zouriel.Workflows.Testing;
using Zouriel.Workflows.Time;

namespace Zouriel.Workflows.Tests;

/// <summary>The working-time helpers behind SLA pauses, and what a kept clock does under the interventions the shared fixtures cannot reach.</summary>
public sealed class SlaPauseCalendarTests
{
    // Monday to Friday 09:00-17:00 UTC, Tuesday 2026-10-27 a holiday.
    private static readonly WorkCalendar Work = new(TimeZoneInfo.Utc, holidays: [new DateOnly(2026, 10, 27)]);
    private static DateTimeOffset T(string s) => DateTimeOffset.Parse(s);

    [Theory]
    [InlineData("2026-10-22T10:00:00Z", "2026-10-22T15:30:00Z", "05:30:00")]   // inside one day
    [InlineData("2026-10-22T16:00:00Z", "2026-10-23T10:00:00Z", "02:00:00")]   // across a night: 1h + 1h
    [InlineData("2026-10-23T16:00:00Z", "2026-10-26T10:00:00Z", "02:00:00")]   // across a weekend
    [InlineData("2026-10-26T16:00:00Z", "2026-10-28T10:00:00Z", "02:00:00")]   // across a holiday (Tuesday counts nothing)
    [InlineData("2026-10-24T10:00:00Z", "2026-10-25T10:00:00Z", "00:00:00")]   // only a weekend
    [InlineData("2026-10-22T10:00:00Z", "2026-10-26T10:00:00Z", "16:00:00")]   // Thu 7h + Fri 8h + Mon 1h
    public void Working_time_between_counts_only_working_hours(string from, string to, string expected)
    {
        Assert.Equal(TimeSpan.Parse(expected), Work.WorkingTimeBetween(T(from), T(to)));
    }

    [Fact]
    public void Time_between_is_zero_when_the_end_is_not_after_the_start_and_plain_without_a_calendar()
    {
        Assert.Equal(TimeSpan.Zero, Work.WorkingTimeBetween(T("2026-10-22T12:00:00Z"), T("2026-10-22T11:00:00Z")));
        Assert.Equal(TimeSpan.FromHours(30), WorkCalendar.Utc.WorkingTimeBetween(T("2026-10-22T12:00:00Z"), T("2026-10-23T18:00:00Z")));
        Assert.Equal(TimeSpan.FromHours(30), Work.WorkingTimeBetween(T("2026-10-22T12:00:00Z"), T("2026-10-23T18:00:00Z"), working: false));
    }

    [Fact]
    public void Adding_working_time_skips_nights_weekends_and_holidays()
    {
        // Thu 16:00 + 10h: 1h Thu, 8h Fri, 1h Mon -> Monday 10:00.
        Assert.Equal(T("2026-10-26T10:00:00Z"), Work.AddTime(T("2026-10-22T16:00:00Z"), TimeSpan.FromHours(10), working: true));
        // From a Saturday it starts on Monday 09:00; the holiday Tuesday is skipped: 10h = Mon 8h + Wed 2h.
        Assert.Equal(T("2026-10-28T11:00:00Z"), Work.AddTime(T("2026-10-24T16:00:00Z"), TimeSpan.FromHours(10), working: true));
        // Plain time ignores the calendar.
        Assert.Equal(T("2026-10-23T02:00:00Z"), Work.AddTime(T("2026-10-22T16:00:00Z"), TimeSpan.FromHours(10)));
        Assert.Equal(T("2026-10-22T10:00:00Z"), Work.AddTime(T("2026-10-22T10:00:00Z"), TimeSpan.Zero, working: true));
    }

    [Theory]
    [InlineData("2026-10-22T10:00:00Z", 3)]
    [InlineData("2026-10-23T16:30:00Z", 9)]
    [InlineData("2026-10-24T03:00:00Z", 17)]   // Saturday start
    [InlineData("2026-10-26T16:59:00Z", 40)]   // across the holiday
    public void Adding_and_measuring_working_time_are_inverses(string from, int hours)
    {
        var start = T(from);
        var end = Work.AddTime(start, TimeSpan.FromHours(hours), working: true);
        Assert.Equal(TimeSpan.FromHours(hours), Work.WorkingTimeBetween(start, end));
    }

    [Fact]
    public void A_time_limit_added_in_working_days_is_measured_in_working_hours()
    {
        // P2D working from Thursday 10:00 is Monday 10:00, which is 16 working hours away; re-adding gives the same instant.
        var start = T("2026-10-22T10:00:00Z");
        var due = Work.Add(start, IsoDuration.Parse("P2D"), working: true);
        Assert.Equal(T("2026-10-26T10:00:00Z"), due);
        Assert.Equal(due, Work.AddTime(start, Work.WorkingTimeBetween(start, due), working: true));
    }
}

public sealed class SlaPauseEngineTests
{
    private static readonly WorkflowActor Rev = WorkflowActor.User("rev1", "Rana", ["reviewer"]);
    private static WorkflowActor Admin => AuditFixTests.Admin;

    // draft -> underReview (SLA P5D, pauses in "submitted" = Awaiting information) -> submitted / withdrawn (On hold, not listed) / approved.
    private static string Lc(string extra = "", string mode = "enforce") => $$"""
    { "specVersion": 1, "id": "lc_s", "key": "slaLc", "kind": "lifecycle", "title": "SLA", "entity": "application",
      "lifecycle": { "field": "status", "mode": "{{mode}}",
        "states": [
          { "id": "d", "key": "draft", "label": "Draft", "category": "initial" },
          { "id": "r", "key": "underReview", "label": "Under review", "category": "active",
            "sla": { "within": "P5D", "pauseOnStatus": ["w"], "pauseOnEvent": ["payment.received"], "resumeOnEvent": ["nightly"], "steps": [] } },
          { "id": "w", "key": "submitted", "label": "Awaiting information", "category": "active" },
          { "id": "h", "key": "withdrawn", "label": "On hold", "category": "active" },
          { "id": "x", "key": "approved", "label": "Approved", "category": "terminal" } ],
        "transitions": [
          { "id": "t1", "key": "start", "label": "Start", "from": ["d"], "to": "r", "who": [ { "role": "reviewer" } ] },
          { "id": "t2", "key": "ask", "label": "Ask", "from": ["r"], "to": "w", "who": [ { "role": "reviewer" } ] },
          { "id": "t3", "key": "back", "label": "Back", "from": ["w", "h"], "to": "r", "who": [ { "role": "reviewer" } ] },
          { "id": "t4", "key": "hold", "label": "Hold", "from": ["w", "r"], "to": "h", "who": [ { "role": "reviewer" } ] },
          { "id": "t5", "key": "approve", "label": "Approve", "from": ["r"], "to": "x", "who": [ { "role": "reviewer" } ] }
          {{extra}} ] } }
    """;

    private static async Task<WorkflowTestHost> PausedAsync(string mode = "enforce")
    {
        var h = await AuditFixTests.HostAsync();
        await h.PublishAsync(Lc(mode: mode));
        await h.Store.CreateRecordAsync("application", "a1", new() { ["title"] = "A" });
        await h.SettleAsync();
        await h.Engine.ApplyTransitionAsync("application", "a1", "start", null, Rev);
        await h.AdvanceAsync(TimeSpan.FromDays(2));
        await h.Engine.ApplyTransitionAsync("application", "a1", "ask", null, Rev);
        await h.SettleAsync();
        return h;
    }

    private static InstanceRecord Record(WorkflowTestHost h) => h.Store.All<InstanceRecord>("instances").Single(i => i.Kind == InstanceKinds.Record);
    private static int Timers(WorkflowTestHost h) => h.Store.All<BookmarkRecord>("bookmarks").Count;
    private static int Count(WorkflowTestHost h, string action) => h.Store.All<AuditRecord>("audit").Count(a => a.Action == action);
    private static bool HasClock(WorkflowTestHost h) => SlaClocks.Read(Record(h)).Count > 0;

    [Fact]
    public async Task A_paused_clock_is_kept_in_the_record_instance_state_and_plans_nothing()
    {
        var h = await PausedAsync();
        Assert.Equal(0, Timers(h));
        var clock = Assert.Single(SlaClocks.Read(Record(h)));
        Assert.Equal("r", clock.Key);
        Assert.Equal(TimeSpan.FromDays(3), clock.Value.Left);
        Assert.Equal(["status"], clock.Value.Reasons);
    }

    [Fact]
    public async Task Force_state_drops_a_paused_clock()
    {
        var h = await PausedAsync();
        await h.Engine.ForceStateAsync("application", "a1", "underReview", "fix", false, Admin);
        await h.SettleAsync();
        Assert.False(HasClock(h));
        Assert.Equal("{}", Record(h).Data);
        // A fresh full clock (5 days), not the 3 days that were left.
        var timer = Assert.Single(h.Store.All<BookmarkRecord>("bookmarks"));
        Assert.Equal(h.Clock.GetUtcNow() + TimeSpan.FromDays(5), timer.DueAt);
        Assert.Equal(0, Count(h, "slaResumed"));
    }

    [Fact]
    public async Task Force_state_with_skipped_steps_drops_a_paused_clock_too()
    {
        var h = await PausedAsync();
        await h.Engine.ForceStateAsync("application", "a1", "withdrawn", "fix", true, Admin);
        Assert.False(HasClock(h));
        await h.Engine.ForceStateAsync("application", "a1", "underReview", "fix", true, Admin);
        Assert.Equal(h.Clock.GetUtcNow() + TimeSpan.FromDays(5), Assert.Single(h.Store.All<BookmarkRecord>("bookmarks")).DueAt);
    }

    [Fact]
    public async Task Cancelling_the_record_instance_drops_the_paused_clock()
    {
        var h = await PausedAsync();
        await h.Engine.CancelAsync(Record(h).Id, "test", Admin);
        Assert.False(HasClock(h));
        Assert.Equal("{}", Record(h).Data);
    }

    [Fact]
    public async Task An_outside_write_that_leaves_the_lifecycle_out_of_sync_drops_a_paused_clock()
    {
        var h = await PausedAsync(mode: "observe");
        // From "Awaiting information" no transition leads to "approved".
        await h.Store.ChangeRecordAsync("application", "a1", new() { ["status"] = "approved" });
        await h.SettleAsync();
        Assert.Equal(InstanceStatus.OutOfSync, Record(h).Status);
        Assert.False(HasClock(h));
        // Back in sync at the state the clock belonged to: a fresh clock, not the paused one.
        await h.Store.ChangeRecordAsync("application", "a1", new() { ["status"] = "underReview" });
        await h.SettleAsync();
        Assert.Equal(h.Clock.GetUtcNow() + TimeSpan.FromDays(5), Assert.Single(h.Store.All<BookmarkRecord>("bookmarks")).DueAt);
    }

    [Fact]
    public async Task An_observed_write_between_listed_and_owner_states_pauses_and_resumes()
    {
        var h = await PausedAsync(mode: "observe");
        // "Back" leads from the awaiting state to review: an outside write is recorded as that transition and resumes the clock.
        await h.Store.ChangeRecordAsync("application", "a1", new() { ["status"] = "underReview" });
        await h.SettleAsync();
        Assert.Equal(1, Count(h, "slaResumed"));
        Assert.Equal(h.Clock.GetUtcNow() + TimeSpan.FromDays(3), Assert.Single(h.Store.All<BookmarkRecord>("bookmarks")).DueAt);
    }

    [Fact]
    public async Task Reaching_a_final_state_drops_every_kept_clock()
    {
        var h = await PausedAsync();
        await h.Engine.ApplyTransitionAsync("application", "a1", "back", null, Rev);
        await h.SettleAsync();
        await h.Engine.SignalAsync("payment.received", "application", "a1", null);
        await h.Engine.ApplyTransitionAsync("application", "a1", "approve", null, Rev);
        Assert.False(HasClock(h));
        Assert.Equal(0, Timers(h));
    }

    [Fact]
    public async Task Migrating_a_paused_record_drops_the_clock_and_starts_the_new_versions_state_fresh()
    {
        var h = await PausedAsync();
        await h.PublishAsync(Lc());
        var inst = Record(h);
        await h.Engine.MigrateAsync(inst.Id, 2, null, Admin);
        Assert.False(HasClock(h));
        Assert.Equal("{}", Record(h).Data);
    }

    [Fact]
    public async Task Archiving_keeps_a_paused_clock_paused_and_restoring_does_not_start_it()
    {
        var h = await PausedAsync();
        await h.Engine.ArchiveAsync("lc_s", Admin);
        Assert.Single(SlaClocks.Read(Record(h)));
        await h.AdvanceAsync(TimeSpan.FromDays(30));
        await h.Engine.RestoreAsync("lc_s", Admin);
        await h.SettleAsync();
        Assert.Equal(0, Timers(h));
        Assert.Equal(0, Count(h, "slaBreached"));
        // Still paused until the record is back: the 3 days that were left count from the return.
        await h.Engine.ApplyTransitionAsync("application", "a1", "back", null, Rev);
        Assert.Equal(h.Clock.GetUtcNow() + TimeSpan.FromDays(3), Assert.Single(h.Store.All<BookmarkRecord>("bookmarks")).DueAt);
    }

    [Fact]
    public async Task Archiving_a_running_clock_that_was_shifted_by_a_pause_restores_it_at_the_same_due_instant()
    {
        var h = await PausedAsync();
        await h.AdvanceAsync(TimeSpan.FromDays(10));
        await h.Engine.ApplyTransitionAsync("application", "a1", "back", null, Rev);
        var due = Assert.Single(h.Store.All<BookmarkRecord>("bookmarks")).DueAt;
        await h.Engine.ArchiveAsync("lc_s", Admin);
        Assert.Equal(0, Timers(h));
        await h.AdvanceAsync(TimeSpan.FromDays(1));
        await h.Engine.RestoreAsync("lc_s", Admin);
        Assert.Equal(due, Assert.Single(h.Store.All<BookmarkRecord>("bookmarks")).DueAt);
        Assert.False(HasClock(h));
    }

    [Fact]
    public async Task Pausing_a_parked_sla_timer_clears_the_incident_and_a_resumed_timer_can_be_parked_retried_and_skipped()
    {
        var h = await AuditFixTests.HostAsync();
        await h.PublishAsync(Lc());
        await h.Store.CreateRecordAsync("application", "a1", new() { ["title"] = "A" });
        await h.SettleAsync();
        await h.Engine.ApplyTransitionAsync("application", "a1", "start", null, Rev);
        await ParkAsync(h);
        Assert.Equal(InstanceStatus.Incident, Record(h).Status);
        // An event pause removes the parked timer and with it the incident.
        await h.Engine.SignalAsync("payment.received", "application", "a1", null);
        Assert.Equal(InstanceStatus.Active, Record(h).Status);
        Assert.Null(Record(h).Error);
        Assert.Equal(0, Timers(h));
        await h.Engine.SignalAsync("nightly", "application", "a1", null);
        // The resumed timer is an ordinary sla timer: parked, it is retried (re-armed) ...
        await ParkAsync(h);
        await h.Engine.RetryAsync(Record(h).Id, Admin);
        var timer = Assert.Single(h.Store.All<BookmarkRecord>("bookmarks"));
        Assert.Equal("sla:r", timer.Purpose);
        Assert.Null(timer.LockedUntil);
        Assert.Equal(InstanceStatus.Active, Record(h).Status);
        // ... or skipped (dropped, the stay's SLA never runs).
        await ParkAsync(h);
        await h.Engine.SkipAsync(Record(h).Id, null, Admin);
        Assert.Equal(0, Timers(h));
        Assert.Equal(InstanceStatus.Active, Record(h).Status);
    }

    /// <summary>Parks the record's SLA timer the way repeated failures do (locked for ever, the instance in incident).</summary>
    private static async Task ParkAsync(WorkflowTestHost h)
    {
        await using var s = await h.Store.BeginAsync();
        var b = h.Store.All<BookmarkRecord>("bookmarks").Single();
        b.LockedUntil = DateTimeOffset.MaxValue;
        b.Attempts = 10;
        b.Error = "boom";
        var r = (await s.GetInstanceAsync(Record(h).Id))!;
        r.Status = InstanceStatus.Incident;
        r.Error = "A timer (sla:r) failed 10 times and was stopped: boom";
        r.ErrorStepId = b.NodeId;
        await s.UpdateBookmarkAsync(b);
        await s.UpdateInstanceAsync(r);
        await s.CommitAsync();
    }

    [Fact]
    public async Task A_definition_without_the_new_fields_leaves_the_record_instance_data_untouched()
    {
        var h = await AuditFixTests.HostAsync();
        await h.PublishAsync(Lc().Replace("\"pauseOnStatus\": [\"w\"], \"pauseOnEvent\": [\"payment.received\"], \"resumeOnEvent\": [\"nightly\"], ", ""));
        await h.Store.CreateRecordAsync("application", "a1", new() { ["title"] = "A" });
        await h.SettleAsync();
        await h.Engine.ApplyTransitionAsync("application", "a1", "start", null, Rev);
        await h.Engine.ApplyTransitionAsync("application", "a1", "ask", null, Rev);
        await h.Engine.ArchiveAsync("lc_s", Admin);
        await h.Engine.RestoreAsync("lc_s", Admin);
        Assert.Equal("{}", Record(h).Data);
        Assert.Equal(0, Count(h, "slaPaused"));
    }

    [Fact]
    public void The_sla_pause_lists_read_and_write_as_plain_json_arrays()
    {
        var def = Definitions.Parse(Lc());
        var sla = def.Lifecycle!.States.Single(s => s.Id == "r").Sla!;
        Assert.Equal(["w"], sla.PauseOnStatus);
        Assert.Equal(["payment.received"], sla.PauseOnEvent);
        Assert.Equal(["nightly"], sla.ResumeOnEvent);
        var json = Definitions.Serialize(def);
        Assert.Contains("\"pauseOnStatus\":[\"w\"]", json);
        var plain = Definitions.Serialize(Definitions.Parse(Lc().Replace("\"pauseOnStatus\": [\"w\"], ", "")));
        Assert.DoesNotContain("pauseOnStatus", plain);
    }
}
