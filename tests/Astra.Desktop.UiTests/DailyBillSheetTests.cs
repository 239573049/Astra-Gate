using Astra.Desktop.Components.Overview;
using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Overview;
using Astra.Desktop.Core.Shell;
using Astra.Desktop.Pages.Overview;
using Astra.Desktop.Platform;
using Astra.Desktop.Shell;
using Astra.Desktop.Views;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;

namespace Astra.Desktop.UiTests;

/// <summary>The overview's daily bill (web DailyBillSheet): the header opens it, both styles draw, and copy / save hand over a 2x PNG.</summary>
public class DailyBillSheetTests
{
    [AvaloniaFact]
    public void The_header_opens_the_bill_and_each_style_renders_at_the_web_export_width()
    {
        var shell = Scenes.BuildShell(Scenes.Fixtures());
        shell.Navigator.Navigate(AppRoute.Overview);
        var window = new Window { Content = new ShellView { DataContext = shell }, Width = 1440, Height = SceneCapture.Height };
        window.Show();
        try
        {
            Settle(shell.Context.Queries);
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == Loc.T("bill.generate"));

            var page = (OverviewPage)shell.Navigator.Current;
            page.OpenBill(BillStyles.Receipt);
            Settle(shell.Context.Queries);
            var sheet = window.GetVisualDescendants().OfType<OverviewView>().Single().BillSheet!;
            Assert.True(sheet.IsOpen);
            Assert.IsType<ReceiptBillView>(sheet.Art);
            using (var receipt = sheet.Snapshot()!)
            {
                // The web's export: the 372 px receipt at pixel ratio 2.
                Assert.Equal(744, receipt.PixelSize.Width);
                Save(receipt, "bill-receipt.png");
            }

            sheet.SetStyle(BillStyles.Manga);
            SceneCapture.Pump(10);
            Assert.Equal(BillStyles.Manga, BillPrefs.Style);
            Assert.IsType<MangaBillView>(sheet.Art);
            using var manga = sheet.Snapshot()!;
            // The 620 px page plus its 8 px hard shadow, at pixel ratio 2.
            Assert.Equal(1256, manga.PixelSize.Width);
            Save(manga, "bill-manga.png");
        }
        finally
        {
            BillPrefs.Style = BillStyles.Receipt;
            window.Close();
        }
    }

    [AvaloniaFact]
    public void The_bill_opens_again_after_it_was_closed()
    {
        // The second click on 「生成账单」 used to close the app: Open() put the kept switch slot and preview into a new panel
        // while the previous one still held them ("already has a visual parent").
        var shell = Scenes.BuildShell(Scenes.Fixtures());
        shell.Navigator.Navigate(AppRoute.Overview);
        var window = new Window { Content = new ShellView { DataContext = shell }, Width = 1440, Height = SceneCapture.Height };
        window.Show();
        try
        {
            Settle(shell.Context.Queries);
            var page = (OverviewPage)shell.Navigator.Current;
            page.OpenBill();
            Settle(shell.Context.Queries);
            var sheet = window.GetVisualDescendants().OfType<OverviewView>().Single().BillSheet!;
            Assert.True(sheet.IsOpen);

            for (var i = 0; i < 2; i++)
            {
                ((Astra.Desktop.Components.Kit.Sheet)sheet.Content!).IsOpen = false;
                SceneCapture.Pump(10);
                page.OpenBill(i == 0 ? BillStyles.Manga : null);
                Settle(shell.Context.Queries);
                Assert.True(sheet.IsOpen);
                Assert.IsType<MangaBillView>(sheet.Art);
            }

            // A language change rebuilds the header around the kept refresh button; twice, with the sheet open.
            page.OnLanguageChanged();
            SceneCapture.Pump(10);
            page.OnLanguageChanged();
            SceneCapture.Pump(10);
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == Loc.T("bill.generate"));
        }
        finally
        {
            BillPrefs.Style = BillStyles.Receipt;
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Copy_and_save_hand_the_png_over_and_say_so()
    {
        var api = Scenes.FixtureApi(Scenes.Fixtures().Handler());
        var queries = new QueryClient(AvaloniaUiDispatcher.Instance);
        var bill = new StatsQueries(api, queries, 0).DailyBill();
        var feedback = new Recorder();
        var target = new Target();
        var sheet = new DailyBillSheet(() => bill, feedback, target);
        var window = new Window { Content = sheet, Width = 1000, Height = 900 };
        window.Show();
        try
        {
            BillPrefs.Style = BillStyles.Manga;
            sheet.Open();
            Settle(queries);
            SceneCapture.Await(sheet.CopyAsync(), TimeSpan.FromSeconds(10));
            SceneCapture.Await(sheet.SaveAsync(), TimeSpan.FromSeconds(10));

            Assert.Equal(1256, target.Copied?.PixelSize.Width);
            Assert.Equal("astra-bill-2026-10-26-manga.png", target.SavedAs);
            Assert.Equal([Loc.T("bill.copied"), Loc.T("bill.saved")], feedback.Successes);

            // A cancelled save says nothing.
            target.Accept = false;
            SceneCapture.Await(sheet.SaveAsync(), TimeSpan.FromSeconds(10));
            Assert.Equal(2, feedback.Successes.Count);
            Assert.Empty(feedback.Errors);
        }
        finally
        {
            BillPrefs.Style = BillStyles.Receipt;
            window.Close();
            queries.Dispose();
        }
    }

    [Fact]
    public void A_copied_bill_is_on_the_mac_pasteboard_at_once_as_png_and_tiff()
    {
        // Avalonia only promised the image (asked for at paste time, after the bitmap was gone): pasting found nothing. The bill is
        // now written outright. A private pasteboard keeps the developer's clipboard out of it.
        if (!OperatingSystem.IsMacOS()) return;
        var name = "io.astra.test." + Guid.NewGuid().ToString("N");
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");
        try
        {
            Assert.True(MacPasteboard.SetImage(png, name));
            Assert.Equal(png.Length, MacPasteboard.DataLength(name, "public.png"));
            Assert.True(MacPasteboard.DataLength(name, "public.tiff") > 0);
        }
        finally
        {
            MacPasteboard.Release(name);
        }
    }

    private static void Settle(QueryClient queries)
    {
        SceneCapture.Await(queries.WhenIdleAsync(), TimeSpan.FromSeconds(20));
        SceneCapture.Pump(20);
        SceneCapture.Await(queries.WhenIdleAsync(), TimeSpan.FromSeconds(20));
        SceneCapture.Pump(10);
    }

    /// <summary>Keeps the rendered bill beside the scene captures, to hold against the web's export.</summary>
    private static void Save(Bitmap png, string name)
    {
        Directory.CreateDirectory(SceneCapture.OutDir);
        png.Save(Path.Combine(SceneCapture.OutDir, name), PngBitmapEncoderOptions.Default);
    }

    private sealed class Target : IBillImageTarget
    {
        public bool Accept { get; set; } = true;
        public PixelSizeHolder? Copied { get; private set; }
        public string? SavedAs { get; private set; }

        public Task<bool> CopyAsync(Bitmap png)
        {
            Copied = new PixelSizeHolder(png.PixelSize);
            return Task.FromResult(Accept);
        }

        public Task<bool> SaveAsync(string suggestedName, Bitmap png)
        {
            if (Accept) SavedAs = suggestedName;
            return Task.FromResult(Accept);
        }
    }

    private sealed record PixelSizeHolder(Avalonia.PixelSize PixelSize);

    private sealed class Recorder : IFeedback
    {
        public List<string> Successes { get; } = [];
        public List<string> Errors { get; } = [];
        public void Success(string title, string? description = null) => Successes.Add(title);
        public void Error(string title, string? description = null) => Errors.Add(title);
        public void Info(string title, string? description = null) { }
        public Task<bool> ConfirmAsync(ConfirmRequest request, CancellationToken ct = default) => Task.FromResult(true);
    }
}
