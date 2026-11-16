using Astra.Api.Contracts;
using Astra.Desktop.Core.Backup;
using Astra.Desktop.Core.I18n;

namespace Astra.Desktop.Tests.Settings;

/// <summary>The Backup &amp; sync form's rules (linked update 38), the same as the web components'.</summary>
public class BackupLogicTests
{
    public BackupLogicTests() => Loc.Language = "en";

    [Theory]
    [InlineData("", false, true)]
    [InlineData("", true, false)]
    [InlineData(null, true, false)]
    [InlineData("short", false, false)]
    [InlineData("short", true, false)]
    [InlineData("12345678", true, true)]
    [InlineData("12345678", false, true)]
    public void An_Export_Needs_A_Long_Enough_Passphrase_And_Keys_Need_One(string? pass, bool keys, bool expected)
        => Assert.Equal(expected, BackupLogic.CanExport(keys, pass));

    [Fact]
    public void An_Export_Without_A_Passphrase_Never_Asks_For_Keys()
    {
        var plain = BackupLogic.ExportInput(true, "");
        Assert.False(plain.IncludeKeys);
        Assert.Null(plain.Passphrase);
        var sealed1 = BackupLogic.ExportInput(true, "12345678");
        Assert.True(sealed1.IncludeKeys);
        Assert.Equal("12345678", sealed1.Passphrase);
    }

    [Theory]
    [InlineData("folder", "", "/tmp/x", "", null)]
    [InlineData("folder", "", " ", "", "sync.folder.folder")]
    [InlineData("webdav", "", "", "", "sync.url.webdav")]
    [InlineData("webdav", "https://dav", "", "", null)]
    [InlineData("s3", "", "", "", "sync.bucket")]
    [InlineData("s3", "", "", "b", null)]
    [InlineData("github", "o/r", "", "", null)]
    [InlineData("github", "", "", "", "sync.url.github")]
    [InlineData("ftp", "x", "x", "x", "sync.kind")]
    public void A_Target_Needs_What_Its_Kind_Uses(string kind, string url, string folder, string bucket, string? missing)
        => Assert.Equal(missing, BackupLogic.MissingTargetField(kind, url, folder, bucket));

    [Fact]
    public void The_Target_Carries_Only_The_Fields_Of_Its_Kind_And_An_Empty_Secret_Keeps_The_Stored_One()
    {
        var folder = BackupLogic.Target("folder", "u", "n", "", " /sync ", "b", "r", true, "main");
        Assert.Equal(("folder", null, null, null, "/sync", null, null, null, null), (folder.Kind, folder.Url, folder.Username, folder.Secret, folder.Folder, folder.Bucket, folder.Region, folder.PathStyle, folder.Branch));
        var s3 = BackupLogic.Target("s3", " https://s3 ", "key", "sec", "", "b", "r", true, "main");
        Assert.Equal(("https://s3", "key", "sec", "b", "r", true, null), (s3.Url, s3.Username, s3.Secret, s3.Bucket, s3.Region, s3.PathStyle, s3.Branch));
        var gh = BackupLogic.Target("github", "o/r", "n", "tok", "d", "b", "r", true, " main ");
        Assert.Equal((null, "main", null), (gh.Username, gh.Branch, gh.Bucket));
    }

    [Theory]
    [InlineData("", false, false)]
    [InlineData("", true, true)]
    [InlineData("1234567", true, false)]
    [InlineData("12345678", false, true)]
    public void Saving_The_Sync_Needs_A_New_Long_Passphrase_Or_A_Stored_One(string pass, bool stored, bool expected)
        => Assert.Equal(expected, BackupLogic.CanSaveSync(pass, stored));

    [Fact]
    public void The_Interval_Is_At_Least_One_And_Falls_Back_To_The_Default()
    {
        Assert.Equal(30, BackupLogic.Interval(30));
        Assert.Equal(BackupLogic.DefaultIntervalMinutes, BackupLogic.Interval(0));
        Assert.Equal(BackupLogic.DefaultIntervalMinutes, BackupLogic.Interval(null));
        Assert.Equal(1, BackupLogic.SyncConfig(BackupLogic.Target("folder", "", "", "", "/f", "", "", false, ""), "", 0, false, false).IntervalMinutes);
    }

    [Fact]
    public void A_Preview_Lists_Only_Sections_That_Would_Change()
    {
        var plan = new BackupPlanDto("2026-11-16", "1.2.3", true, "merge", ["providers", "tokens"],
            [
                new BackupSectionPlanDto("providers", 1, 2, 0, 4, [], false),
                new BackupSectionPlanDto("tokens", 0, 0, 0, 3, [], false),
            ], ["a note"], false, null);
        var lines = BackupLogic.PlanLines(plan);
        Assert.Equal(3, lines.Count);
        Assert.Contains("1.2.3", lines[0]);
        Assert.Contains("1 added", lines[1]);
        Assert.Contains("2 changed", lines[1]);
        Assert.Equal("a note", lines[2]);
        Assert.DoesNotContain(lines, l => l.StartsWith("Tokens"));
    }

    [Fact]
    public void A_Restore_Reports_Written_Removed_The_Safety_Copy_And_What_Was_Skipped_Or_Failed()
    {
        var plan = new BackupPlanDto("t", "v", false, "merge", [], [], [], false, null);
        var full = BackupLogic.RestoreLines(new BackupRestoreDto(plan, 5, 2, ["a", "b"], ["e"], "astra-x-restore.astra-backup", null));
        Assert.Equal(4, full.Count);
        Assert.Contains("5", full[0]);
        Assert.Contains("astra-x-restore.astra-backup", full[1]);
        Assert.Contains("2", full[2]);
        Assert.Contains("1", full[3]);
        Assert.Single(BackupLogic.RestoreLines(new BackupRestoreDto(plan, 0, 0, [], [], null, null)));
    }

    [Fact]
    public void Sources_Name_Their_Kind()
    {
        Assert.Equal("file", BackupLogic.FileSource("{}", "", "merge").Source);
        Assert.Null(BackupLogic.FileSource("{}", "", "merge").Passphrase);
        var auto = BackupLogic.AutoSource("a.astra-backup", "pw", "replace");
        Assert.Equal(("auto", "a.astra-backup", "pw", "replace"), (auto.Source, auto.Name, auto.Passphrase, auto.Mode));
    }

    [Theory]
    [InlineData(10L, "10 B")]
    [InlineData(2048L, "2.0 KB")]
    public void Sizes_Are_Short(long size, string text) => Assert.Equal(text, BackupLogic.FormatSize(size));

    [Fact]
    public void An_Unknown_Section_Shows_Its_Id()
        => Assert.Equal("future-thing", BackupLogic.SectionLabel("future-thing"));
}
