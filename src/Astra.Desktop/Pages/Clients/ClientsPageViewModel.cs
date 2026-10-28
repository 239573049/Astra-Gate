using System.ComponentModel;
using Astra.Api.Contracts;
using Astra.Desktop.Components.Clients;
using Astra.Desktop.Components.Layout;
using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.Clients;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Overview;
using Astra.Desktop.Core.Shell;
using Astra.Desktop.Shell;
using TKCode.Arc;
using TKCode.Arc.Icons.Lucide;
using ArcButton = TKCode.Arc.Button;

namespace Astra.Desktop.Pages.Clients;

/// <summary>
/// The Clients page (web/src/pages/ClientsPage.tsx, route <c>/clients[/:kind]</c>): the client list, the selected client's draft and every
/// action of its panel — check for updates, enable (through the preview sheet), disable and the drift follow-up, repair, account pin,
/// provider list edits, and the toast when an install / update ends. The view draws; this owns the state that outlives a redraw.
/// Picking a client changes <see cref="SelectedKind"/> in place (the web replaces the URL; here the page stays, so drafts of the page
/// survive and nothing is fetched again).
/// </summary>
public sealed class ClientsPageViewModel : PageViewModel
{
    private readonly PageContext _ctx;
    private readonly ArcButton _checkButton;
    private readonly Dictionary<string, ClientDraft> _drafts = new(StringComparer.Ordinal);
    private string? _selected;
    private bool _checkedOnce;
    private Query<ClientInstallJobDto?>? _job;
    private IDisposable? _jobToken;
    private string? _jobState;

    /// <summary>Creates the page; <paramref name="param"/> is the client kind to show first.</summary>
    public ClientsPageViewModel(PageContext context, string? param) : base(AppRoute.Clients, param, context)
    {
        _ctx = context;
        Api = new ClientsApi(context.Api, context.Queries);
        Clients = Api.Clients();
        Providers = new ListQueries(context.Api, context.Queries).Providers();
        _selected = param;
        Observe(Clients);
        Observe(Providers);
        Observe(context.Tokens.Tokens);
        Subscribe(Clients, OnClients);

        _checkButton = ClientUi.Button(Loc.T("clients.checkUpdates"), () => _ = CheckNowAsync(), icon: ClientUi.Icon(LucideIconKind.RefreshCw));
        SetHeaderActions(new ActionGroup(8, _checkButton));
    }

    /// <summary>The shown view's panel (the visual tests drive it).</summary>
    internal ClientPanelView? Panel { get; set; }

    /// <summary>The sheet the view opened last (the visual tests wait for it).</summary>
    internal Astra.Desktop.Components.Kit.Sheet? OpenSheet { get; set; }

    /// <summary>The page's queries and writes.</summary>
    public ClientsApi Api { get; }

    /// <summary>The client list (polls while an install runs).</summary>
    public Query<List<ClientInfoDto>> Clients { get; }

    /// <summary>The providers (bindings, account pins).</summary>
    public Query<List<ProviderDto>> Providers { get; }

    /// <summary>The tokens (the token select).</summary>
    public Query<List<TokenDto>> Tokens => _ctx.Tokens.Tokens;

    /// <inheritdoc />
    public override string? Subtitle => Loc.T("clients.subtitle");

    /// <inheritdoc />
    public override void OnLanguageChanged()
    {
        base.OnLanguageChanged();
        _checkButton.Content = ClientUi.HRow(6, ClientUi.Icon(LucideIconKind.RefreshCw), new Avalonia.Controls.TextBlock { Text = Loc.T("clients.checkUpdates"), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
        SelectionChanged?.Invoke();
    }

    /// <summary>The shown client's kind (the route's, else Codex).</summary>
    public string SelectedKind => ClientsLogic.SelectedKind(_selected, Clients.Data);

    /// <summary>The shown client, when the list holds it.</summary>
    public ClientInfoDto? Selected => Clients.Data?.FirstOrDefault(c => c.Kind == SelectedKind);

    /// <summary>The zone the page shows times in.</summary>
    public TimeSpan Zone => TimeSpan.FromMinutes(LocalTime.Of(_ctx.Clock).OffsetMinutes);

    /// <summary>Raised when the selection or the drafts' derived state changed and the panel must be drawn again.</summary>
    public event Action? SelectionChanged;

    /// <summary>The view opens the enable / apply preview.</summary>
    public event Action<ClientInfoDto, ClientDraft>? EnableRequested;

    /// <summary>The view opens the backups sheet.</summary>
    public event Action<ClientInfoDto>? BackupsRequested;

    /// <summary>The view opens the install sheet.</summary>
    public event Action<ClientInfoDto, InstallSheetMode>? InstallRequested;

    /// <summary>Shows <paramref name="kind"/>.</summary>
    public void Select(string kind)
    {
        if (kind == SelectedKind) return;
        _selected = kind;
        WatchJob();
        SelectionChanged?.Invoke();
    }

    /// <summary>The draft of <paramref name="client"/>, synced with its saved state.</summary>
    public ClientDraft DraftOf(ClientInfoDto client)
    {
        if (!_drafts.TryGetValue(client.Kind, out var draft)) _drafts[client.Kind] = draft = new ClientDraft(client);
        else if (!ReferenceEquals(draft.Client, client)) draft.Sync(client);
        return draft;
    }

    /// <summary>Opens the preview of enabling / re-applying the shown client with its draft.</summary>
    public void BeginEnable(ClientInfoDto client) => EnableRequested?.Invoke(client, DraftOf(client));

    /// <summary>Opens the backups sheet.</summary>
    public void BeginBackups(ClientInfoDto client) => BackupsRequested?.Invoke(client);

    /// <summary>Opens the install sheet in <paramref name="mode"/>.</summary>
    public void BeginInstall(ClientInfoDto client, InstallSheetMode mode) => InstallRequested?.Invoke(client, mode);

    /// <summary>Opens the token list.</summary>
    public void OpenTokens() => _ctx.Navigator.Navigate("Tokens");

    /// <summary>Opens the providers page with the add wizard (web <c>/providers?new=1</c>).</summary>
    public void AddProvider() => _ctx.Navigator.Navigate("Providers", MenuCommandRouter.NewProviderParam);

    /// <inheritdoc />
    protected override void OnActivated()
    {
        WatchJob();
        // Latest versions are fetched once per visit; the server keeps them six hours, so this is usually instant.
        if (_checkedOnce) return;
        _checkedOnce = true;
        _ = CheckAsync(false, toast: false);
    }

    /// <inheritdoc />
    protected override void OnDeactivated() => UnwatchJob();

    private Task CheckNowAsync() => CheckAsync(true, toast: true);

    private async Task CheckAsync(bool force, bool toast)
    {
        _checkButton.IsLoading = true;
        try
        {
            var list = await Api.CheckUpdatesAsync(force);
            if (!toast) return;
            var count = ClientsLogic.UpdatesFound(list);
            if (count > 0) _ctx.Feedback.Info(Loc.T("clients.updatesFound", ("count", count)));
            else _ctx.Feedback.Success(Loc.T("clients.allUpToDate"));
        }
        catch (Exception e) when (e is ApiException or HttpRequestException or TaskCanceledException)
        {
            if (toast) _ctx.Feedback.Error(e.Message);
        }
        finally
        {
            _checkButton.IsLoading = false;
        }
    }

    private void OnClients(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Query<int>.Data)) WatchJob();
    }

    // The install job of the shown client is watched while it runs (or the page is up and it is busy), so its end is announced even when
    // the sheet was closed (web: the InstallSheet stays mounted with its query enabled while the client is busy).
    private void WatchJob()
    {
        var client = Selected;
        var want = client?.Install?.Busy == true ? Api.InstallJob(client.Kind) : _job?.Data?.State == "running" ? _job : null;
        if (ReferenceEquals(want, _job)) return;
        UnwatchJob();
        if (want is null) return;
        _job = want;
        _jobState = want.Data?.State;
        want.PropertyChanged += OnJob;
        _jobToken = want.Activate();
    }

    private void UnwatchJob()
    {
        if (_job is not null) _job.PropertyChanged -= OnJob;
        _jobToken?.Dispose();
        _jobToken = null;
        _job = null;
    }

    private void OnJob(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(Query<int>.Data) || _job?.Data is not { } job) return;
        var outcome = InstallLogic.Transition(_jobState, job);
        _jobState = job.State;
        if (outcome == InstallOutcome.None) return;
        _ctx.Queries.Invalidate("clients");
        var client = Clients.Data?.FirstOrDefault(c => c.Kind == job.Kind);
        var name = ClientCatalog.Name(job.Kind, client?.Name);
        switch (outcome)
        {
            case InstallOutcome.NotEffective: _ctx.Feedback.Info(Loc.T("clients.notEffectiveToast", ("name", name))); break;
            case InstallOutcome.Installed: _ctx.Feedback.Success(Loc.T("clients.installedToast", ("name", name))); break;
            case InstallOutcome.Updated: _ctx.Feedback.Success(Loc.T("clients.updatedToast", ("name", name))); break;
            case InstallOutcome.Uninstalled: _ctx.Feedback.Success(Loc.T("clients.uninstalledToast", ("name", name))); break;
        }
    }

    /// <summary>Starts watching the job right after a run was started from the sheet.</summary>
    public void JobStarted() => WatchJob();

    // ---------- panel actions ----------

    /// <summary>Saves the account pin of the primary binding (web <c>saveAccount</c>).</summary>
    public async Task<bool> SaveAccountAsync(ClientInfoDto client, string account)
    {
        if (client.ProviderId is null) return false;
        return await Try(async () =>
        {
            await Api.SetBindingAsync(client.Kind, client.ProviderId, account);
            _ctx.Feedback.Success(Loc.T("common.saved"));
        });
    }

    /// <summary>
    /// Saves the provider list (web <c>ProviderBindings.save</c>): the toast is "models synced" for a client whose config lists the models
    /// and is enabled, else <paramref name="done"/>. False when the save failed (the caller reverts its optimistic order).
    /// </summary>
    public Task<bool> SaveBindingsAsync(ClientInfoDto client, IReadOnlyList<ClientBindingDto> next, string done) => Try(async () =>
    {
        await Api.SetBindingsAsync(client.Kind, next);
        _ctx.Feedback.Success(ClientsLogic.ModelListClients.Contains(client.Kind) && client.Enabled
            ? Loc.T("clients.providersModelsSynced", ("client", ClientCatalog.Name(client.Kind, client.Name)))
            : done);
    });

    /// <summary>Disables (restoring the original values), then offers to force-restore what drifted (web <c>doDisable</c>).</summary>
    public async Task<bool> DisableAsync(ClientInfoDto client)
    {
        var name = ClientCatalog.Name(client.Kind, client.Name);
        if (!await _ctx.Feedback.ConfirmAsync(new ConfirmRequest(Loc.T("clients.disableConfirm", ("name", name)), Loc.T("clients.disableDetail"), Loc.T("clients.disable")))) return false;
        ClientDisableDto result;
        try
        {
            result = await Api.DisableAsync(client.Kind, force: false);
        }
        catch (Exception e) when (e is ApiException or HttpRequestException or TaskCanceledException)
        {
            _ctx.Feedback.Error(e.Message);
            return false;
        }
        if (result.Drifted.Count == 0)
        {
            _ctx.Feedback.Success(Loc.T("clients.disabledToast", ("name", name)));
            return true;
        }
        var force = await _ctx.Feedback.ConfirmAsync(new ConfirmRequest(
            Loc.T("clients.driftTitle"), Loc.T("clients.driftDetail", ("keys", string.Join(", ", result.Drifted))), Loc.T("clients.forceRestore"), Destructive: true));
        if (!force) return true;
        return await Try(async () =>
        {
            await Api.DisableAsync(client.Kind, force: true);
            _ctx.Feedback.Success(Loc.T("clients.disabledToast", ("name", name)));
        });
    }

    /// <summary>Repairs the config files the client cannot read (web <c>repair</c>).</summary>
    public Task<bool> RepairAsync(ClientInfoDto client) => Try(async () =>
    {
        await Api.RepairAsync(client.Kind);
        _ctx.Feedback.Success(Loc.T("clients.repair.done", ("name", ClientCatalog.Name(client.Kind, client.Name))));
    });

    /// <summary>Opens the config file's directory (desktop: the system file manager), else copies the path (web <c>openConfigDir</c>).</summary>
    public async Task OpenConfigDirAsync(string path, Func<string, Task<bool>> copy)
    {
        if (await _ctx.Host.OpenPathAsync(ClientsLogic.DirnameOf(path))) return;
        if (await copy(path)) _ctx.Feedback.Info(Loc.T("common.copied"));
    }

    private async Task<bool> Try(Func<Task> run)
    {
        try
        {
            await run();
            return true;
        }
        catch (Exception e) when (e is ApiException or HttpRequestException or TaskCanceledException)
        {
            _ctx.Feedback.Error(e.Message);
            return false;
        }
    }

    /// <inheritdoc />
    public override async Task WhenSettledAsync(CancellationToken ct = default)
    {
        for (var i = 0; i < 3; i++)
        {
            await _ctx.Queries.WhenIdleAsync(ct: ct);
            await Task.Delay(40, ct);
        }
    }
}
