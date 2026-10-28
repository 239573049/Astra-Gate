using System.Text.Json.Nodes;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Shell;
using Astra.Desktop.Views;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TKCode.Arc;
using ArcButton = TKCode.Arc.Button;

namespace Astra.Desktop.UiTests;

/// <summary>
/// The shell's and the kit's own states (round 2): the clients-outdated banner (web ConnectionBanner), the server-down banner, the sidebar's
/// remote-access line (card and rail), and the kit corrections — accent primary buttons, CJK empty states, exact sheet widths, the 420 px
/// confirm and the web's error alert toast.
/// </summary>
public static partial class Scenes
{
    static partial void AddShell(Dictionary<string, Func<Scene>> d)
    {
        d["shell__clients-outdated"] = () => Page(AppRoute.Tokens, Fixtures().Override("/api/clients", ShellOutdatedClients()));
        d["shell__remote-on"] = () => Page(AppRoute.Tokens, Fixtures().Override("/api/tunnel", ShellTunnelOn()));
        d["shell__remote-on-rail"] = () => Page(AppRoute.Tokens, Fixtures().Override("/api/tunnel", ShellTunnelOn()), collapsed: true);
        d["shell__down"] = () => ShellDownScene(collapsed: false);
        d["shell__down-rail"] = () => ShellDownScene(collapsed: true);

        d["kit__gallery"] = () => new ScrollViewer { Content = KitGallerySmoke.Build() };
        d["kit__buttons"] = () => ShellKitPanel(ShellButtons());
        d["kit__empty-cjk"] = () => ShellKitPanel(ShellEmptyStates());
        d["kit__sheet-640"] = () => ShellSheetScene(640);
        d["kit__sheet-780"] = () => ShellSheetScene(780);
        d["kit__confirm"] = () => ShellOverScene(view =>
        {
            _ = ConfirmDialog.Build("删除令牌「default」？", "使用它的客户端会立即失去访问权限。", "删除", null, destructive: true).ShowAsync(view);
        });
        d["kit__error-toast"] = () => ShellOverScene(view =>
        {
            ToastPresenter.Attach(view);
            ErrorToasts.Show(view, "保存失败：provider \"openai\" 不存在");
        });
    }

    /// <summary>Two enabled clients whose configs still point at an old address.</summary>
    private static string ShellOutdatedClients()
    {
        var list = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ui", "default", "clients.json")))!.AsArray();
        foreach (var c in list.Take(2)) c!["configOutdated"] = true;
        return list.ToJsonString();
    }

    private static string ShellTunnelOn()
    {
        var tunnel = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ui", "default", "tunnel.json")))!.AsObject();
        tunnel["enabled"] = true;
        tunnel["state"] = "running";
        tunnel["publicUrl"] = "https://quiet-river.trycloudflare.com";
        return tunnel.ToJsonString();
    }

    private static Scene ShellDownScene(bool collapsed)
    {
        var shell = BuildShell(Fixtures(), collapsed);
        shell.Connection.AssumeDown("无法连接到 127.0.0.1:17321（连接被拒绝）");
        shell.Navigator.Navigate(AppRoute.Tokens);
        return new Scene(new ShellView { DataContext = shell }, async () =>
        {
            await shell.Context.Queries.WhenIdleAsync();
            await shell.Navigator.Current.WhenSettledAsync();
        });
    }

    /// <summary>The Tokens page in the shell with something opened over it once it settled.</summary>
    private static Scene ShellOverScene(Action<Control> open)
    {
        var shell = BuildShell(Fixtures());
        shell.Navigator.Navigate(AppRoute.Tokens);
        var view = new ShellView { DataContext = shell };
        return new Scene(view, async () =>
        {
            await shell.Context.Queries.WhenIdleAsync();
            await shell.Navigator.Current.WhenSettledAsync();
            open(view);
            await Task.Delay(300);
        });
    }

    private static Scene ShellSheetScene(double width) => ShellOverScene(view =>
    {
        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(new TextBlock { Text = $"SheetWidth = {width}", TextWrapping = TextWrapping.Wrap });
        body.Children.Add(new TextBlock { Text = "宽度与 web 一致：min(100vw - 2rem, width)", TextWrapping = TextWrapping.Wrap });
        var cancel = new ArcButton { Variant = ButtonVariant.Secondary, Content = "取消" };
        var save = new ArcButton { Variant = ButtonVariant.Primary, Content = "保存" };
        var hint = new TextBlock { Text = "左侧的说明（mr-auto）", VerticalAlignment = VerticalAlignment.Center };
        var sheet = new Sheet { Title = "宽表单", Description = "kit Sheet 的精确宽度", SheetWidth = width, Content = body, Footer = [SheetFooter.Split(hint, [cancel, save])] };
        _ = sheet.ShowAsync(view);
    });

    private static Control ShellKitPanel(Control content)
    {
        var host = new Border { Padding = new Avalonia.Thickness(32), Child = content };
        host.Token(Border.BackgroundProperty, TKCode.Arc.DesignTokens.ArcDesignTokenKind.Background);
        return host;
    }

    private static Control ShellButtons()
    {
        var rows = new StackPanel { Spacing = 16 };
        foreach (var size in new[] { ControlSize.Small, ControlSize.Medium })
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(new ArcButton { Variant = ButtonVariant.Primary, Size = size, Content = "新建令牌" });
            row.Children.Add(new ArcButton { Variant = ButtonVariant.Primary, Size = size, Content = "已禁用", IsEnabled = false });
            row.Children.Add(new ArcButton { Variant = ButtonVariant.Primary, Size = size, Content = "保存中", IsLoading = true });
            row.Children.Add(new ArcButton { Variant = ButtonVariant.Secondary, Size = size, Content = "取消" });
            row.Children.Add(new ArcButton { Variant = ButtonVariant.Ghost, Size = size, Content = "更多" });
            row.Children.Add(new ArcButton { Variant = ButtonVariant.Danger, Size = size, Content = "删除" });
            rows.Children.Add(row);
        }
        return rows;
    }

    private static Control ShellEmptyStates()
    {
        var grid = new Avalonia.Controls.Primitives.UniformGrid { Columns = 2 };
        grid.Children.Add(new EmptyStateView("没有请求记录", "经过 Astra 的每次请求都会记录在这里，包括逐项 token 与计费明细。"));
        grid.Children.Add(new EmptyStateView("No requests yet", "Every request that passes through Astra is logged here, with a per-token breakdown and its bill."));
        grid.Children.Add(new EmptyStateView("还没有令牌", "创建一个令牌，让客户端通过 Astra 调用模型。", action: new ArcButton { Variant = ButtonVariant.Primary, Content = "新建令牌" }));
        grid.Children.Add(new ErrorState { Message = "请求失败：HTTP 500（服务器内部错误，请查看日志后重试）" });
        return grid;
    }
}
