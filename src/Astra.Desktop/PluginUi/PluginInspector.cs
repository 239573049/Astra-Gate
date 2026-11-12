using System.Text.Json;
using System.Text.Json.Nodes;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Components.Settings;
using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.I18n;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Icons.Lucide;

namespace Astra.Desktop.PluginUi;

/// <summary>
/// The dev inspector of a view (design/plugin-v2 06 §2; web <c>plugin-ui/Inspector.tsx</c>): for a dev plugin's view only —
/// the session's tree, state, timeline of round trips, the plugin's host calls and log, and the warnings.
/// </summary>
internal static class PluginInspector
{
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    /// <summary>Whether the session can be inspected (GET …/inspect answers only for a dev plugin).</summary>
    public static async Task<bool> AvailableAsync(ApiClient api, string sessionId)
    {
        try
        {
            return await api.GetAsync<JsonObject>($"/api/plugins/v2/ui/sessions/{ApiClient.Enc(sessionId)}/inspect") is not null;
        }
        catch (Exception e) when (e is HttpRequestException or ApiException)
        {
            return false;
        }
    }

    /// <summary>The small button over the view that opens the inspector.</summary>
    public static Control Button(Visual owner, ApiClient api, string sessionId)
    {
        var b = Ui.Button(Loc.T("pluginUi.inspector.title"), () => _ = ShowAsync(owner, api, sessionId), ButtonVariant.Ghost, LucideIconKind.Bug);
        b.HorizontalAlignment = HorizontalAlignment.Right;
        b.VerticalAlignment = VerticalAlignment.Top;
        b.ZIndex = 10;
        return b;
    }

    private static async Task ShowAsync(Visual owner, ApiClient api, string sessionId)
    {
        string[] tabs = ["tree", "state", "timeline", "calls", "warnings"];
        var tab = "tree";
        var body = new SelectableTextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap, FontSize = 11 };
        body.Token(TextBlock.FontFamilyProperty, ArcDesignTokenKind.MonoFontFamily);
        var head = KitStyle.Text("", ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextMuted);
        var strip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        JsonObject? data = null;
        JsonObject? detail = null;

        void Draw()
        {
            strip.Children.Clear();
            foreach (var t in tabs)
            {
                var name = t;
                strip.Children.Add(Ui.Button(Loc.T("pluginUi.inspector." + t), () =>
                {
                    tab = name;
                    Draw();
                }, t == tab ? ButtonVariant.Secondary : ButtonVariant.Ghost));
            }
            if (data is null) return;
            head.Text = $"{data["plugin"]} · {data["view"]} · rev {data["rev"]}";
            body.Text = tab switch
            {
                "tree" => Json(data["tree"]),
                "state" => Json(new JsonObject { ["state"] = data["state"]?.DeepClone(), ["local"] = data["local"]?.DeepClone(), ["actions"] = data["actions"]?.DeepClone() }),
                "timeline" => string.Join("\n", (data["timeline"] as JsonArray ?? []).OfType<JsonObject>().Reverse().Select(e =>
                    $"{e["action"],-18} queue {e["queueMs"]}ms  handler {e["handlerMs"]}ms  render {e["renderMs"]}ms  diff {e["diffMs"]}ms  ops {e["ops"]}")),
                "calls" => Json(new JsonObject { ["activity"] = detail?["activity"]?.DeepClone(), ["log"] = detail?["lines"]?.DeepClone() }),
                _ => string.Join("\n", (data["warnings"] as JsonArray ?? []).Select(w => w?.ToString())),
            };
        }

        using var stop = new CancellationTokenSource();
        async Task PullAsync()
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    data = await api.GetAsync<JsonObject>($"/api/plugins/v2/ui/sessions/{ApiClient.Enc(sessionId)}/inspect", stop.Token);
                    if (data?["plugin"]?.GetValue<string>() is { } plugin)
                        detail = await api.GetAsync<JsonObject>($"/api/plugins/v2/{ApiClient.Enc(plugin)}", stop.Token);
                    Dispatcher.UIThread.Post(Draw);
                    await Task.Delay(1000, stop.Token);
                }
                catch (Exception e) when (e is HttpRequestException or ApiException or OperationCanceledException)
                {
                    return;
                }
            }
        }

        Draw();
        var sheet = new Sheet
        {
            Title = Loc.T("pluginUi.inspector.title"),
            Content = new StackPanel { Spacing = 10, Children = { head, strip, new ScrollViewer { MaxHeight = 560, Content = body } } },
        };
        _ = PullAsync();
        await sheet.ShowAsync(owner);
        stop.Cancel();
    }

    private static string Json(JsonNode? node) => node?.ToJsonString(Pretty) ?? "null";
}
