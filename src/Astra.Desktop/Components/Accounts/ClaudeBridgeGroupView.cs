using Astra.Api.Contracts;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Components.Settings;
using Astra.Desktop.Core.Accounts;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Api;
using Astra.Desktop.Shell;
using Avalonia;
using Avalonia.Controls;
using TKCode.Arc;
using SettingsGroup = Astra.Desktop.Components.Kit.SettingsGroup;

namespace Astra.Desktop.Components.Accounts;

/// <summary>
/// "Through the claude program" on a Claude subscription's accounts tab (web <c>ClaudeBridgePanel</c>, linked update 36): the <c>claude</c> program
/// that was found, how it is run, the accounts that are answered through it (each with a sign-in check and a sign-in button) and the form that adds one.
/// Shown for the Claude subscription provider only. The desktop mirrors the web's panel; the run list is a line a run, no sheet.
/// </summary>
internal sealed class ClaudeBridgeGroupView : LiveView
{
    private readonly ProviderDto _provider;
    private readonly ClaudeBridgeApi _api;
    private readonly AccountsApi _accounts;
    private readonly Query<List<ProviderAccountDto>> _list;
    private readonly Dictionary<string, string> _signIn = [];
    private readonly Dictionary<string, string> _login = [];
    private Input _binary = null!;
    private Input _maxRuns = null!;
    private Input _idle = null!;
    private Input _name = null!;
    private Input _dir = null!;
    private Input _proxy = null!;
    private SettingsGroup _status = null!;
    private SettingsGroup _accountRows = null!;
    private bool _filled;

    /// <summary>Creates the group for <paramref name="provider"/>.</summary>
    public ClaudeBridgeGroupView(PageContext ctx, ProviderDto provider) : base(ctx)
    {
        _provider = provider;
        _api = new ClaudeBridgeApi(ctx.Api, ctx.Queries);
        _accounts = new AccountsApi(ctx.Api, ctx.Queries);
        _list = _accounts.Accounts(provider.Id);
        Watch(_api.Status);
        Watch(_list);
    }

    /// <inheritdoc />
    protected override void Build()
    {
        // The inputs are built once and sit in groups that are never drawn again: a poll only redraws the rows that have no input in them
        // (re-adding a kept control to a new container throws "already has a visual parent").
        _binary = new Input { Width = 280, PlaceholderText = Loc.T("claudeBridge.binaryPathPlaceholder"), Size = ControlSize.Small };
        _maxRuns = new Input { Width = 80, Size = ControlSize.Small };
        _idle = new Input { Width = 80, Size = ControlSize.Small };
        _name = new Input { Width = 180, Size = ControlSize.Small };
        _dir = new Input { Width = 280, PlaceholderText = "~/.claude-work", Size = ControlSize.Small };
        _proxy = new Input { Width = 220, PlaceholderText = "http://127.0.0.1:7890", Size = ControlSize.Small };
        _status = new SettingsGroup { Title = Loc.T("claudeBridge.title"), Footer = Loc.T("claudeBridge.hint") };
        _accountRows = new SettingsGroup();
        var settings = RowBuilder.Group(Loc.T("claudeBridge.settings"), null,
            RowBuilder.Row(Loc.T("claudeBridge.binaryPath"), null, _binary),
            RowBuilder.Row(Loc.T("claudeBridge.maxRuns"), null, _maxRuns),
            RowBuilder.Row(Loc.T("claudeBridge.idleMinutes"), null, _idle, Ui.Button(Loc.T("common.save"), () => _ = SaveAsync())));
        var add = RowBuilder.Group(Loc.T("claudeBridge.add"), null,
            RowBuilder.Row(Loc.T("claudeBridge.add.name"), null, _name),
            RowBuilder.Row(Loc.T("claudeBridge.add.configDir"), Loc.T("claudeBridge.add.configDirHint"), _dir),
            RowBuilder.Row(Loc.T("claudeBridge.add.proxy"), null, _proxy, Ui.Button(Loc.T("claudeBridge.add"), () => _ = AddAsync(), ButtonVariant.Primary)));
        Content = new StackPanel { Margin = new Thickness(0, 20, 0, 0), Children = { _status, settings, _accountRows, add } };
        _filled = false;
    }

    /// <inheritdoc />
    protected override void Refresh()
    {
        if (_provider.TemplateId != "claude-subscription" || _api.Status.Data is not { } s)
        {
            IsVisible = false;
            return;
        }
        IsVisible = true;
        if (!_filled)
        {
            _binary.Text = s.Settings.BinaryPath ?? "";
            _maxRuns.Text = s.Settings.MaxRuns.ToString();
            _idle.Text = s.Settings.IdleMinutes.ToString();
            _filled = true;
        }
        _status.Rows.Clear();
        var found = RowBuilder.Row(ClaudeBridgeLogic.FoundText(s),
            s.Found && s.Path is not null ? Loc.T("claudeBridge.path", ("path", s.Path), ("source", Loc.T("claudeBridge.source." + (s.Source ?? "path")))) : null);
        if (s.Runs.Count > 0) found.Trailing.Add(Ui.Badge(Loc.T("claudeBridge.runs", ("n", s.Runs.Count)), "neutral"));
        _status.Rows.Add(found);
        foreach (var r in s.Runs)
            _status.Rows.Add(RowBuilder.Row($"{r.Pid?.ToString() ?? "—"} · {r.Model}", string.IsNullOrEmpty(r.ConfigDir) ? Loc.T("claudeBridge.defaultDir") : r.ConfigDir,
                new TextBlock { Text = Loc.T("claudeBridge.run." + r.State) }));
        if (s.ClientPolicy == "claude-code-only") _status.Rows.Add(Ui.GroupAlert(AlertTone.Info, Loc.T("claudeBridge.clientPolicy")));
        _status.Rows.Add(Ui.GroupNote(Loc.T("claudeBridge.cost")));
        _accountRows.Rows.Clear();
        foreach (var a in (_list.Data ?? []).Where(a => a.Kind == "claude_bridge")) _accountRows.Rows.Add(AccountRow(a, s.Found));
    }

    private Control AccountRow(ProviderAccountDto a, bool found)
    {
        var detail = _login.TryGetValue(a.Id, out var login) ? login
            : _signIn.TryGetValue(a.Id, out var who) ? who
            : Loc.T("claudeBridge.dir", ("dir", string.IsNullOrEmpty(a.ClaudeConfigDir) ? Loc.T("claudeBridge.defaultDir") : a.ClaudeConfigDir));
        return RowBuilder.Row(string.IsNullOrEmpty(a.DisplayName) ? a.Id : a.DisplayName, detail,
            Ui.Button(Loc.T("claudeBridge.signin.check"), () => _ = CheckAsync(a), ButtonVariant.Ghost, enabled: found),
            Ui.Button(Loc.T("claudeBridge.login"), () => _ = LoginAsync(a), enabled: found));
    }

    private async Task SaveAsync()
    {
        if (!int.TryParse(_maxRuns.Text, out var runs) || !int.TryParse(_idle.Text, out var idle)) return;
        if (await Host.TryAsync(() => _api.UpdateSettingsAsync(new ClaudeBridgeSettingsPatch((_binary.Text ?? "").Trim(), runs, idle))))
        {
            _filled = false;
            Context.Feedback.Success(Loc.T("claudeBridge.saved"));
        }
    }

    private async Task AddAsync()
    {
        var added = await Host.TryAsync(() => _api.AddAccountAsync(new ClaudeBridgeAccountRequest(_provider.Id, _name.Text?.Trim(), (_dir.Text ?? "").Trim(), string.IsNullOrWhiteSpace(_proxy.Text) ? null : _proxy.Text.Trim())));
        if (added is null) return;
        _name.Text = _dir.Text = _proxy.Text = "";
        Context.Feedback.Success(Loc.T("claudeBridge.added"));
    }

    private async Task CheckAsync(ProviderAccountDto a)
    {
        var s = await Host.TryAsync(() => _api.SignInAsync(a.Id));
        if (s is null) return;
        _signIn[a.Id] = ClaudeBridgeLogic.SignInText(s);
        RequestRefresh();
    }

    private async Task LoginAsync(ProviderAccountDto a)
    {
        var start = await Host.TryAsync(() => _api.StartLoginAsync(a.Id));
        if (start is null) return;
        // the program opens the browser on a desktop; the address is shown for a server and polled until the sign-in is over
        _login[a.Id] = start.Url is { Length: > 0 } url ? Loc.T("claudeBridge.login.waiting") + " " + url : Loc.T("claudeBridge.login.waiting");
        RequestRefresh();
        for (var i = 0; i < 300; i++)
        {
            await Task.Delay(2000);
            var state = await Host.TryAsync(() => _api.LoginStateAsync(a.Id));
            if (state is null || state.State == "waiting") continue;
            _login.Remove(a.Id);
            if (state.State == "done")
            {
                Context.Feedback.Success(Loc.T("claudeBridge.login.done"));
                await CheckAsync(a);
            }
            else
            {
                Context.Feedback.Error(Loc.T("claudeBridge.login.failed", ("message", state.Message ?? "")));
            }
            RequestRefresh();
            return;
        }
    }
}
