using System.Net;
using System.Text;
using Astra.Desktop.Components.Clients;
using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.Clients;
using Astra.Desktop.Core.Shell;
using Astra.Desktop.Platform;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;

namespace Astra.Desktop.UiTests;

/// <summary>The profiles sheet re-renders when its list loads or changes; its name box must survive that (one visual parent only).</summary>
public class ProfileSheetTests
{
    [AvaloniaFact]
    public void The_sheet_renders_again_when_the_list_arrives_without_reparenting_the_name_box()
    {
        using var queries = new QueryClient(AvaloniaUiDispatcher.Instance);
        var api = new ApiClient("http://fixture", new Profiles());
        var window = new Window { Width = 1000, Height = 800 };
        window.Show();
        try
        {
            var sheet = ProfileSheets.Show(window, new ProfileSheetContext(new ProfilesApi(api, queries), new Quiet()));
            SceneCapture.Pump(5);
            SceneCapture.Await(queries.WhenIdleAsync(), TimeSpan.FromSeconds(20));
            SceneCapture.Pump(20); // the loaded list renders a second time; it used to throw here
            Assert.True(sheet.IsOpen);
        }
        finally
        {
            window.Close();
        }
    }

    private sealed class Profiles : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]", Encoding.UTF8, "application/json") });
    }

    private sealed class Quiet : IFeedback
    {
        public void Success(string title, string? description = null) { }
        public void Error(string title, string? description = null) { }
        public void Info(string title, string? description = null) { }
        public Task<bool> ConfirmAsync(ConfirmRequest request, CancellationToken ct = default) => Task.FromResult(true);
    }
}
