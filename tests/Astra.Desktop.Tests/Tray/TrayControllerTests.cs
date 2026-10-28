using System.Text.Json.Nodes;
using Astra.Desktop.Core.Shell;
using Astra.Desktop.Core.Tray;

namespace Astra.Desktop.Tests.Tray;

/// <summary>The main-process glue of desktop/src/main.ts (refreshTray, runServiceAction, trayCallbacks, runTrayPanelCommand, the tray IPC) over fakes.</summary>
[Collection("Loc")]
public sealed class TrayControllerTests : IDisposable
{
    private readonly string _saved = Astra.Desktop.Core.I18n.Loc.Language;

    public TrayControllerTests() => Astra.Desktop.Core.I18n.Loc.Language = "en";

    public void Dispose() => Astra.Desktop.Core.I18n.Loc.Language = _saved;

    private sealed class FakeService : ITrayService
    {
        public TrayProbe Probe { get; set; } = new(true, 17321, false);
        public List<string> Calls { get; } = [];
        public Exception? Fail { get; set; }
        public TaskCompletionSource? Gate { get; set; }
        public IReadOnlyList<TrayClient> Clients { get; set; } = [new("codex", "Codex", true, "p1")];
        public IReadOnlyList<TrayProvider> Providers { get; set; } = [new("p1", "OpenAI", true), new("p2", "Kimi", true)];

        public Task<TrayProbe> ProbeAsync() => Task.FromResult(Probe);
        public async Task StartAsync() { Calls.Add("start"); await Run(); Probe = Probe with { Running = true, Port = 17321 }; }
        public async Task StopAsync() { Calls.Add("stop"); await Run(); Probe = new(false, null, false); }
        public async Task RestartAsync() { Calls.Add("restart"); await Run(); }
        public Task<IReadOnlyList<TrayClient>> ClientsAsync() => Task.FromResult(Clients);
        public Task<IReadOnlyList<TrayProvider>> ProvidersAsync() => Task.FromResult(Providers);
        public async Task SwitchProviderAsync(string clientKind, string providerId) { Calls.Add($"switch {clientKind} {providerId}"); await Run(); }

        private async Task Run()
        {
            if (Gate is not null) await Gate.Task;
            if (Fail is not null) throw Fail;
        }
    }

    private sealed class FakeShell : ITrayShell
    {
        public List<string> Calls { get; } = [];
        public IReadOnlyList<TrayMenuItem>? Menu { get; private set; }
        public int MenuBuilds { get; private set; }
        public string Tooltip { get; private set; } = "";
        public string Title { get; private set; } = "";

        public void SetMenu(IReadOnlyList<TrayMenuItem> menu) { Menu = menu; MenuBuilds++; }
        public void SetTooltip(string tooltip) => Tooltip = tooltip;
        public void SetTitle(string title) => Title = title;
        public void PopUpMenu() => Calls.Add("menu");
        public void ShowPanel() => Calls.Add("show-panel");
        public void HidePanel() => Calls.Add("hide-panel");
        public void SetPanelHeight(double height) => Calls.Add($"height {height}");
        public void ShowMainWindow(string? command) => Calls.Add("window " + (command ?? ""));
        public void Copy(string text) => Calls.Add("copy " + text);
        public void OpenUrl(string url) => Calls.Add("open " + url);
        public void OpenLogs() => Calls.Add("logs");
        public void SetOpenAtLogin(bool enabled) => Calls.Add($"login {enabled}");
        public Task CheckUpdatesAsync() { Calls.Add("updates"); return Task.CompletedTask; }
        public void ShowError(string message, string detail) => Calls.Add($"error {message}: {detail}");
        public void Quit() => Calls.Add("quit");
    }

    private static (TrayController C, FakeService Service, FakeShell Shell, InMemoryPrefsFile File) Create(string platform = "darwin")
    {
        var service = new FakeService();
        var shell = new FakeShell();
        var file = new InMemoryPrefsFile();
        var c = new TrayController(file, service, shell, () => false, () => false, platform, "1.2.3");
        return (c, service, shell, file);
    }

    [Fact]
    public async Task Prefs_are_stored_under_tray_in_electrons_shape_and_broadcast()
    {
        var (c, _, _, file) = Create();
        Assert.True(TrayPrefsJson.Same(TrayPrefs.Default, await c.GetPrefsAsync()));
        TrayPrefs? seen = null;
        c.PrefsChanged += p => seen = p;
        var stored = await c.SetPrefsAsync(new TrayPrefs(TrayTitleMode.Cost, new Dictionary<TraySection, bool> { [TraySection.Chart] = false }));
        Assert.Equal(TrayTitleMode.Cost, stored.Title);
        Assert.False(stored.Sections[TraySection.Chart]);
        Assert.True(stored.Sections[TraySection.Overview]);
        Assert.Same(stored, seen);
        Assert.Equal("cost", file.Values["tray"]!["title"]!.GetValue<string>());
        Assert.Equal(8, file.Values["tray"]!["sections"]!.AsObject().Count);
        Assert.Equal(TrayTitleMode.Cost, (await c.GetPrefsAsync()).Title);
    }

    [Fact]
    public void Close_hint_is_shown_once()
    {
        var (c, _, _, file) = Create();
        Assert.True(c.TakeCloseHint());
        Assert.False(c.TakeCloseHint());
        Assert.True(file.Values["closeHintShown"]!.GetValue<bool>());
        file.Values["closeHintShown"] = JsonValue.Create(false);
        Assert.True(c.TakeCloseHint());
    }

    [Fact]
    public async Task Refresh_rebuilds_the_menu_only_when_the_state_changed()
    {
        var (c, service, shell, _) = Create();
        var panel = new List<TrayPanelState>();
        c.StateChanged += panel.Add;
        await c.RefreshAsync();
        await c.RefreshAsync();
        Assert.Equal(1, shell.MenuBuilds);
        Assert.Single(panel);
        Assert.Equal("Astra — running on port 17321", shell.Tooltip);
        Assert.Contains(shell.Menu!, i => i.Label == "Codex" && i.Sublabel == "OpenAI");
        await c.RefreshAsync(force: true);
        Assert.Equal(2, shell.MenuBuilds);
        Assert.Single(panel);
        service.Probe = new TrayProbe(false, null, true);
        await c.RefreshAsync();
        Assert.Equal(3, shell.MenuBuilds);
        Assert.Equal(new TrayPanelState(false, null, null, false, null), panel[^1]);
        // Down: no clients are asked for, so the switch block disappears.
        Assert.DoesNotContain(shell.Menu!, i => i.Label == "Codex");
    }

    [Fact]
    public async Task One_service_action_at_a_time_with_the_activity_shown()
    {
        var (c, service, shell, _) = Create();
        await c.RefreshAsync();
        var panel = new List<TrayPanelState>();
        c.StateChanged += panel.Add;
        service.Gate = new TaskCompletionSource();
        var stop = c.CommandAsync(TrayCommand.Stop);
        Assert.Equal(TrayActivity.Stopping, c.State.Activity);
        Assert.Equal("Stopping service…", shell.Menu![1].Label);
        // A second action while one runs is ignored; a poll mid-transition keeps the activity.
        await c.CommandAsync(TrayCommand.Restart);
        await c.RefreshAsync();
        Assert.Equal(TrayActivity.Stopping, c.State.Activity);
        service.Gate.SetResult();
        await stop;
        Assert.Equal(["stop"], service.Calls);
        Assert.Null(c.State.Activity);
        Assert.False(c.State.Running);
        Assert.Equal([TrayActivity.Stopping, null], panel.Select(p => p.Activity));
        Assert.Contains(shell.Menu!, i => i.Label == "Start Service");
    }

    [Fact]
    public async Task A_failed_action_shows_an_error_and_clears_the_activity()
    {
        var (c, service, shell, _) = Create();
        service.Fail = new InvalidOperationException("port busy");
        await c.CommandAsync(TrayCommand.Start);
        Assert.Contains("error Failed to start the Astra service: port busy", shell.Calls);
        Assert.Null(c.State.Activity);
    }

    [Fact]
    public async Task Panel_commands_close_the_panel_before_opening_windows()
    {
        var (c, _, shell, _) = Create();
        await c.CommandAsync(TrayCommand.Open);
        await c.CommandAsync(TrayCommand.OpenSettings);
        await c.CommandAsync(TrayCommand.Menu);
        await c.CommandAsync(TrayCommand.Hide);
        await c.CommandAsync(TrayCommand.CheckUpdates);
        await c.CommandAsync(TrayCommand.Quit);
        Assert.Equal(
            ["hide-panel", "window ", "hide-panel", "window settings:tray", "hide-panel", "menu", "hide-panel", "hide-panel", "updates", "quit"],
            shell.Calls);
    }

    [Fact]
    public async Task Menu_actions()
    {
        var (c, service, shell, _) = Create();
        await c.RefreshAsync();
        await c.OnMenuAsync(new(TrayMenuVerb.Navigate, "requests"));
        await c.OnMenuAsync(new(TrayMenuVerb.CopyApiAddress));
        await c.OnMenuAsync(new(TrayMenuVerb.OpenInBrowser));
        await c.OnMenuAsync(new(TrayMenuVerb.OpenLogs));
        await c.OnMenuAsync(new(TrayMenuVerb.ToggleOpenAtLogin, Flag: true));
        await c.OnMenuAsync(new(TrayMenuVerb.ShowPanel));
        Assert.Equal(["window nav:requests", "copy http://127.0.0.1:17321", "open http://127.0.0.1:17321/", "logs", "login True", "show-panel"], shell.Calls);
        var builds = shell.MenuBuilds;
        service.Fail = new InvalidOperationException("nope");
        await c.OnMenuAsync(new(TrayMenuVerb.SwitchProvider, "codex", "p2"));
        Assert.Contains("switch codex p2", service.Calls);
        Assert.Contains("error Failed to switch provider for codex: nope", shell.Calls);
        // Forced rebuild: the native radio item already moved.
        Assert.True(shell.MenuBuilds > builds);
    }

    [Fact]
    public async Task Title_and_tooltip_line()
    {
        var (c, _, shell, _) = Create();
        await c.RefreshAsync();
        TrayTitleText? seen = null;
        c.TitleChanged += t => seen = t;
        c.SetTitle(" 7\n", "7 calls today");
        Assert.Equal("7", shell.Title);
        Assert.Equal("Astra — running on port 17321 · 7 calls today", shell.Tooltip);
        Assert.Equal(new TrayTitleText("7", "7 calls today"), seen);
        c.SetTitle("", "");
        Assert.Equal("Astra — running on port 17321", shell.Tooltip);
    }

    [Fact]
    public void Resize_and_visibility()
    {
        var (c, _, shell, _) = Create();
        var seen = new List<bool>();
        c.VisibilityChanged += seen.Add;
        c.Resize(412.3);
        c.Resize(double.NaN);
        c.OnPanelVisibility(true);
        c.OnPanelVisibility(true);
        c.OnPanelVisibility(false);
        Assert.Equal(["height 413"], shell.Calls);
        Assert.Equal([true, false], seen);
    }
}
