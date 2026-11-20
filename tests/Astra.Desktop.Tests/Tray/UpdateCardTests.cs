using Astra.Desktop.Core.Shell;
using Astra.Desktop.Core.Tray;
using Astra.Desktop.Core.Update;

namespace Astra.Desktop.Tests.Tray;

/// <summary>The states of the bottom-right update card (<see cref="UpdateCards"/>).</summary>
public class UpdateCardTests
{
    private static readonly UpdateDialogLabels Zh = UpdateDialogLabels.For("zh");
    private static readonly UpdateDialogLabels En = UpdateDialogLabels.For("en");

    private static UpdateCardAction[] Actions(UpdateCardModel m) => m.Buttons.Select(b => b.Action).ToArray();

    [Fact]
    public void The_reminder_offers_update_later_and_skip_with_the_notes()
    {
        var card = UpdateCards.Available(Zh, new UpdateSummary("…", UpdateAction.DesktopDmg, "0.15.0", "  修复若干问题  "), reminder: true);
        Assert.Equal(UpdateCardKind.Available, card.Kind);
        Assert.Equal("Astra 0.15.0 现已可用", card.Title);
        Assert.Equal("修复若干问题", card.Notes);
        Assert.Equal([UpdateCardAction.Update, UpdateCardAction.Later, UpdateCardAction.Skip], Actions(card));
        Assert.True(card.Buttons[0].Primary);
        Assert.Equal(UpdateCardAction.Later, card.Dismiss);
        Assert.Null(card.AutoClose);
        Assert.False(card.ShowProgress);
    }

    [Fact]
    public void The_trays_check_offers_no_skip_and_names_what_it_updates()
    {
        var server = UpdateCards.Available(En, new UpdateSummary("…", UpdateAction.Server, "0.15.0"), reminder: false);
        Assert.Equal([UpdateCardAction.Update, UpdateCardAction.Later], Actions(server));
        Assert.Equal(En.UpdateServer, server.Buttons[0].Label);
        Assert.Equal(En.NpmDesktopHint, server.Message);

        var download = UpdateCards.Available(En, new UpdateSummary("…", UpdateAction.DesktopDownload, "0.15.0"), reminder: false);
        Assert.Equal(UpdateCardAction.Download, download.Buttons[0].Action);
        Assert.Equal(En.DownloadHint, download.Message);
    }

    [Fact]
    public void A_check_result_is_up_to_date_failed_with_retry_or_the_offer()
    {
        var upToDate = UpdateCards.CheckResult(En, new UpdateSummary("Astra is up to date.", UpdateAction.None));
        Assert.Equal(UpdateCardKind.UpToDate, upToDate.Kind);
        Assert.Equal(UpdateCards.ResultLifetime, upToDate.AutoClose);
        Assert.Empty(upToDate.Buttons);

        var failed = UpdateCards.CheckResult(En, new UpdateSummary("Update check failed: offline", UpdateAction.None, Error: "offline"));
        Assert.Equal(UpdateCardKind.Failed, failed.Kind);
        Assert.Equal("offline", failed.Message);
        Assert.Equal([UpdateCardAction.Retry], Actions(failed));

        Assert.Equal(UpdateCardKind.Available, UpdateCards.CheckResult(En, new UpdateSummary("…", UpdateAction.DesktopDmg, "1.0.0")).Kind);
    }

    [Fact]
    public void A_download_shows_percent_and_megabytes_and_can_go_to_the_background()
    {
        var card = UpdateCards.Progress(En, new UpdateProgress(UpdateStage.Downloading, "0.15.0", 13_002_342, 42_000_000));
        Assert.Equal("Updating to Astra 0.15.0", card.Title);
        Assert.Equal(En.Downloading, card.Message);
        Assert.True(card.ShowProgress);
        Assert.Equal(13_002_342 / 42_000_000.0, card.Fraction!.Value, 6);
        Assert.Equal("30% · 12.4 / 40.1 MB", card.ProgressText);
        Assert.Equal([UpdateCardAction.Background], Actions(card));
        Assert.Equal(UpdateCardAction.Background, card.Dismiss);
    }

    [Fact]
    public void An_unknown_size_is_an_indeterminate_bar_with_the_bytes()
    {
        var card = UpdateCards.Progress(En, new UpdateProgress(UpdateStage.Downloading, "1.0.0", 5 * 1048576));
        Assert.True(card.ShowProgress);
        Assert.Null(card.Fraction);
        Assert.Equal("5.0 MB", card.ProgressText);
    }

    [Theory]
    [InlineData(UpdateStage.Preparing)]
    [InlineData(UpdateStage.Verifying)]
    [InlineData(UpdateStage.Installing)]
    [InlineData(UpdateStage.Restarting)]
    public void Steps_after_the_download_are_indeterminate(UpdateStage stage)
    {
        var card = UpdateCards.Progress(Zh, new UpdateProgress(stage, "1.0.0"));
        Assert.Equal(UpdateCardKind.Progress, card.Kind);
        Assert.True(card.ShowProgress);
        Assert.Null(card.Fraction);
        Assert.Null(card.ProgressText);
    }

    [Fact]
    public void The_end_is_updated_restarting_or_failed_with_retry()
    {
        var done = UpdateCards.Progress(Zh, new UpdateProgress(UpdateStage.Done, "0.15.0"));
        Assert.Equal((UpdateCardKind.Done, UpdateCardTone.Success, "已更新到 Astra 0.15.0"), (done.Kind, done.Tone, done.Title));
        Assert.Equal(UpdateCards.ResultLifetime, done.AutoClose);

        var relaunch = UpdateCards.Progress(Zh, new UpdateProgress(UpdateStage.Relaunching, "0.15.0"));
        Assert.Null(relaunch.Dismiss);
        Assert.Empty(relaunch.Buttons);
        Assert.Equal(1.0, relaunch.Fraction);

        var failed = UpdateCards.Progress(En, new UpdateProgress(UpdateStage.Failed, "0.15.0", Error: "Checksum mismatch"));
        Assert.Equal((UpdateCardKind.Failed, UpdateCardTone.Danger, "Checksum mismatch"), (failed.Kind, failed.Tone, failed.Message));
        Assert.Equal([UpdateCardAction.Retry], Actions(failed));
        Assert.Null(failed.AutoClose);
    }
}
