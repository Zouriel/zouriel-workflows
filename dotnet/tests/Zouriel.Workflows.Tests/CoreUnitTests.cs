using System.Text.Json.Nodes;
using Zouriel.Workflows.Audit;
using Zouriel.Workflows.Forms;
using Zouriel.Workflows.Json;
using Zouriel.Workflows.Storage;
using Zouriel.Workflows.Time;

namespace Zouriel.Workflows.Tests;

public sealed class ValuesTests
{
    [Theory]
    [InlineData(0, "0")]
    [InlineData(1.5, "1.5")]
    [InlineData(-12, "-12")]
    [InlineData(1e21, "1e+21")]
    [InlineData(1e-7, "1e-7")]
    [InlineData(123456789012, "123456789012")]
    [InlineData(0.000001, "0.000001")]
    [InlineData(0.1 + 0.2, "0.30000000000000004")]
    public void Numbers_are_written_like_javascript(double n, string expected) => Assert.Equal(expected, Values.NumberText(n));

    [Fact]
    public void Canonical_json_sorts_keys_and_escapes_like_rfc8785()
    {
        var v = Values.FromJson("""{"b":[1,true,null],"a":"x\u0001\"\n","é":2}""");
        Assert.Equal("{\"a\":\"x\\u0001\\\"\\n\",\"b\":[1,true,null],\"é\":2}", Values.Canonical(v));
    }

    [Fact]
    public void Normalize_turns_clr_values_into_workflow_values()
    {
        Assert.Equal(3.0, Values.Normalize(3m));
        Assert.Equal("2026-01-02", Values.Normalize(new DateOnly(2026, 1, 2)));
        Assert.Equal("2026-01-02T03:04:05.000Z", Values.Normalize(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc)));
        Assert.Equal("Monday", Values.Normalize(DayOfWeek.Monday));
    }
}

public sealed class AuditChainTests
{
    [Fact]
    public void Hashes_match_the_shared_fixture_byte_for_byte()
    {
        var fx = JsonNode.Parse(File.ReadAllText(Path.Combine(SpecPaths.Fixtures("audit"), "chain.json")))!;
        foreach (var item in fx["entries"]!.AsArray())
        {
            var entry = Values.FromJson(item!["entry"]);
            Assert.Equal(item["canonical"]!.GetValue<string>(), Values.Canonical(entry));
            var hash = AuditWriter.Sha256Hex(item["prevHash"]!.GetValue<string>() + "\n" + Values.Canonical(entry));
            Assert.Equal(item["hash"]!.GetValue<string>(), hash);
        }
    }

    [Fact]
    public void Records_hash_the_same_object_the_fixture_describes()
    {
        var rec = new AuditRecord
        {
            Seq = 3, Id = "e3", At = DateTimeOffset.Parse("2026-10-19T10:30:00Z"), ActorType = "system", ActorName = "System", Category = "lifecycle",
            Action = "transitioned", SubjectType = "record", SubjectId = "a1", Entity = "application", EntityId = "a1", CorrelationId = "c9", InstanceId = "r_1",
            DefinitionId = "lc_1", Before = """{"state":"draft"}""", After = """{"state":"submitted"}""",
            Data = """{"transition":"submit","input":{"secret":"***","n":-12,"ok":true}}""", CausationId = "i_1",
        };
        var fx = JsonNode.Parse(File.ReadAllText(Path.Combine(SpecPaths.Fixtures("audit"), "chain.json")))!;
        var third = fx["entries"]![2]!;
        Assert.Equal(third["canonical"]!.GetValue<string>(), Values.Canonical(AuditWriter.HashedObject(rec)));
    }

    [Fact]
    public void Verification_finds_a_tampered_entry_and_a_missing_one()
    {
        var entries = new List<AuditRecord>();
        var prev = AuditWriter.GenesisHash;
        for (var i = 1; i <= 4; i++)
        {
            var r = new AuditRecord { Seq = i, Id = $"e{i}", At = DateTimeOffset.UnixEpoch, Category = "c", Action = "a", SubjectType = "s", SubjectId = "x", CorrelationId = "x", PrevHash = prev };
            r.Hash = AuditWriter.ComputeHash(r);
            prev = r.Hash;
            entries.Add(r);
        }
        Assert.True(AuditVerifier.Verify(entries).Valid);
        entries[1].Action = "changed";
        var broken = AuditVerifier.Verify(entries);
        Assert.False(broken.Valid);
        Assert.Equal(2, broken.BrokenAtSeq);
        entries[1].Action = "a";
        var gap = entries.Where(e => e.Seq != 3).ToList();
        Assert.False(AuditVerifier.Verify(gap).Valid);
        Assert.True(AuditVerifier.Verify(gap, [(3, 3)]).Valid);
    }
}

public sealed class TimeTests
{
    private static readonly WorkCalendar Work = new(TimeZoneInfo.Utc, holidays: [new DateOnly(2026, 10, 27)]);

    [Theory]
    [InlineData("2026-10-23T16:00:00Z", "PT2H", "2026-10-26T10:00:00Z")] // Friday afternoon → Monday
    [InlineData("2026-10-26T08:00:00Z", "PT1H", "2026-10-26T10:00:00Z")] // before hours
    [InlineData("2026-10-26T10:00:00Z", "P1D", "2026-10-28T10:00:00Z")]  // skips the holiday on Tuesday
    [InlineData("2026-10-24T12:00:00Z", "P1DT1H", "2026-10-28T10:00:00Z")] // Saturday → Monday 09:00, +1 day past the holiday, +1h
    public void Working_time_skips_nights_weekends_and_holidays(string from, string add, string expected)
    {
        var result = Work.Add(DateTimeOffset.Parse(from), IsoDuration.Parse(add), working: true);
        Assert.Equal(DateTimeOffset.Parse(expected), result);
    }

    [Fact]
    public void Calendar_days_keep_the_local_time_across_daylight_saving()
    {
        var london = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
        var cal = new WorkCalendar(london, hasWorkingTime: false);
        var start = new DateTimeOffset(2026, 10, 24, 9, 0, 0, TimeSpan.FromHours(1)); // 09:00 BST
        var next = cal.Add(start, IsoDuration.Parse("P2D"));
        Assert.Equal(new DateTimeOffset(2026, 10, 26, 9, 0, 0, TimeSpan.Zero), next); // 09:00 GMT
    }

    [Theory]
    [InlineData("P3D", true)]
    [InlineData("PT0S", true)]
    [InlineData("-P1D", true)]
    [InlineData("P", false)]
    [InlineData("3 days", false)]
    [InlineData("PT", false)]
    public void Durations_parse_strictly(string text, bool ok) => Assert.Equal(ok, IsoDuration.TryParse(text, out _));

    [Theory]
    [InlineData("0 8 * * 1-5", "2026-10-23T08:30:00Z", "2026-10-26T08:00:00Z")]
    [InlineData("*/15 * * * *", "2026-10-23T08:31:00Z", "2026-10-23T08:45:00Z")]
    [InlineData("0 0 1 * *", "2026-10-23T08:30:00Z", "2026-11-01T00:00:00Z")]
    [InlineData("30 9 * * 0", "2026-10-23T08:30:00Z", "2026-10-25T09:30:00Z")]
    public void Cron_finds_the_next_run(string expr, string after, string expected) =>
        Assert.Equal(DateTimeOffset.Parse(expected), Cron.Parse(expr).Next(DateTimeOffset.Parse(after), TimeZoneInfo.Utc));

    [Theory]
    [InlineData("0 8 * *")]
    [InlineData("61 * * * *")]
    [InlineData("0 8 * * 9")]
    [InlineData("5-1 * * * *")]
    public void Cron_rejects_bad_schedules(string expr) => Assert.False(Cron.TryParse(expr, out _, out _));
}

public sealed class FormValidatorTests
{
    private static readonly JsonObject Form = JsonNode.Parse("""
    { "schemaVersion": 1, "id": "f", "title": "F", "pages": [
      { "id": "p1", "elements": [
        { "kind": "field", "id": "f_type", "key": "type", "type": "radio", "label": "Type", "required": true },
        { "kind": "field", "id": "f_email", "key": "email", "type": "email", "label": "Email" },
        { "kind": "field", "id": "f_reason", "key": "reason", "type": "textarea", "label": "Why?",
          "required": { "when": { "ref": "f_type", "op": "eq", "value": { "const": "other" } } },
          "validators": [ { "id": "v1", "type": "minLength", "params": { "min": 5 } } ] },
        { "kind": "field", "id": "f_hidden", "key": "secret", "type": "text", "label": "Secret", "required": true,
          "visibility": { "action": "show", "when": { "ref": "f_type", "op": "eq", "value": { "const": "x" } } } },
        { "kind": "group", "id": "g", "key": "items", "label": "Items", "repeat": { "min": 1, "max": 2 }, "elements": [
          { "kind": "field", "id": "f_qty", "key": "qty", "type": "number", "label": "Qty", "required": true, "validators": [ { "id": "v2", "type": "max", "params": { "max": 9 }, "message": "Too many {label}" } ] } ] } ] } ] }
    """)!.AsObject();

    private static List<string> Errors(string json) =>
        new FormValidator().Validate(Form, (Dictionary<string, object?>)Values.FromJson(json)!, "2026-10-19").Select(e => $"{e.Path}: {e.Message}").ToList();

    [Fact]
    public void Required_conditional_and_built_in_checks_use_the_form_builder_wording()
    {
        var errors = Errors("""{ "type": "other", "email": "nope", "reason": "abc", "items": [ { "qty": 12 }, {} ] }""");
        Assert.Equal(
        [
            "email: Enter an email address like name@example.com.",
            "reason: Why? must be at least 5 characters.",
            "items[0].qty: Too many Qty",
            "items[1].qty: Qty is required.",
        ], errors);
    }

    [Fact]
    public void Missing_answers_and_hidden_fields()
    {
        Assert.Equal(["type: Type is required.", "items: Add at least 1 entries to Items."], Errors("{}"));
        Assert.Empty(Errors("""{ "type": "staff", "items": [ { "qty": 1 } ] }"""));
    }
}
