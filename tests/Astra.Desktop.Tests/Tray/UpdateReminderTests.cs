using System.Text.Json.Nodes;
using Astra.Desktop.Core.Tray;

namespace Astra.Desktop.Tests.Tray;

/// <summary>The update reminder's circuit breaker, case for case as desktop/tests/updateReminder.test.ts.</summary>
public sealed class UpdateReminderTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeMilliseconds(1_761_480_000_000);

    [Fact]
    public void A_new_version_reminds_and_records_a_fresh_budget()
    {
        Assert.Null(UpdateReminder.Decide(null, "1.2.0", T0));
        var s = UpdateReminder.Record(null, "1.2.0", T0);
        Assert.Equal($$"""{"version":"1.2.0","count":1,"lastRemindedAt":{{T0.ToUnixTimeMilliseconds()}},"skippedVersion":null}""", s.ToJsonString());
    }

    [Fact]
    public void The_same_version_cools_down_for_a_day_then_stops_after_three()
    {
        var s = UpdateReminder.Record(null, "1.2.0", T0);
        Assert.Equal(ReminderSuppression.Cooldown, UpdateReminder.Decide(s, "1.2.0", T0.AddHours(23)));
        Assert.Equal(ReminderSuppression.Cooldown, UpdateReminder.Decide(s, "1.2.0", T0.AddHours(-1)));
        Assert.Null(UpdateReminder.Decide(s, "1.2.0", T0.AddHours(24)));
        s = UpdateReminder.Record(s, "1.2.0", T0.AddHours(24));
        s = UpdateReminder.Record(s, "1.2.0", T0.AddHours(48));
        Assert.Equal(3, s["count"]!.GetValue<long>());
        Assert.Equal(ReminderSuppression.Limit, UpdateReminder.Decide(s, "1.2.0", T0.AddDays(10)));
        // A newer version starts over.
        Assert.Null(UpdateReminder.Decide(s, "1.3.0", T0.AddDays(10)));
        Assert.Equal(1, UpdateReminder.Record(s, "1.3.0", T0.AddDays(10))["count"]!.GetValue<long>());
    }

    [Fact]
    public void Skipping_a_version_silences_exactly_that_version()
    {
        var s = UpdateReminder.Skip(UpdateReminder.Record(null, "1.2.0", T0), "1.2.0");
        Assert.Equal(ReminderSuppression.Skipped, UpdateReminder.Decide(s, "1.2.0", T0.AddDays(5)));
        Assert.Null(UpdateReminder.Decide(s, "1.2.1", T0.AddDays(5)));
    }

    [Fact]
    public void Malformed_state_is_read_tolerantly()
    {
        var raw = JsonNode.Parse("""{"version":5,"count":"x","lastRemindedAt":1.5e12}""");
        Assert.Null(UpdateReminder.Decide(raw, "1.0.0", T0));
        Assert.Equal(1, UpdateReminder.Record(raw, "1.0.0", T0)["count"]!.GetValue<long>());
    }

    [Fact]
    public void Notes_are_trimmed_and_cut_at_600_characters()
    {
        Assert.Null(UpdateDialogLabels.Notes("  "));
        Assert.Equal("a", UpdateDialogLabels.Notes(" a\n"));
        var cut = UpdateDialogLabels.Notes(new string('x', 700))!;
        Assert.Equal(601, cut.Length);
        Assert.EndsWith("…", cut);
        Assert.Equal("Astra 1.0 is available", UpdateDialogLabels.For("en").Message("1.0"));
        Assert.Equal("Astra 1.0 现已可用", UpdateDialogLabels.For("zh").Message("1.0"));
    }
}
