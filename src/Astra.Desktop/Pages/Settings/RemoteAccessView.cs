using Astra.Api.Contracts;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Components.Settings;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Settings;
using Astra.Desktop.Shell;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Icons.Lucide;
using SettingsGroup = Astra.Desktop.Components.Kit.SettingsGroup;
using SettingsRow = Astra.Desktop.Components.Kit.SettingsRow;
using ArcButton = TKCode.Arc.Button;

namespace Astra.Desktop.Pages.Settings;

/// <summary>
/// Settings › Remote access (web <c>RemoteAccessSettings</c>): a Cloudflare Tunnel Astra runs (cloudflared as a child process). The switch
/// with its state, address and anything blocking it; how it connects (a temporary address or the user's own tunnel); what remote requests
/// reach; where cloudflared comes from; and cloudflared's own output. Drawn once the status arrives, then updated in place so a token being
/// typed survives the polls (1.5 s while it comes up, 15 s while it runs).
/// </summary>
public sealed class RemoteAccessView : LiveView
{
    private readonly SettingsApi _api;
    private readonly RemoteAckStore _ack;
    private ContentControl _host = null!;
    private Parts? _parts;
    private bool _toggling, _updating;

    /// <summary>Creates the view.</summary>
    public RemoteAccessView(PageContext context, SettingsApi api, RemoteAckStore? ack = null) : base(context)
    {
        _api = api;
        _ack = ack ?? RemoteAckStore.Shared;
        Watch(api.Tunnel);
        Watch(api.Auth);
    }

    /// <inheritdoc />
    protected override void Build()
    {
        _parts = null;
        _host = new ContentControl { MaxWidth = 680, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        Content = _host;
    }

    /// <inheritdoc />
    protected override void Refresh()
    {
        var s = _api.Tunnel.Data;
        if (s is null)
        {
            _parts = null;
            _host.Content = _api.Tunnel.IsError ? Ui.ErrorText(UiHost.ErrorText(_api.Tunnel.Error!)) : new Border { Padding = new Thickness(0, 32), Child = Ui.Spinner() };
            return;
        }
        if (!s.Supported)
        {
            _parts = null;
            _host.Content = new Alert { Tone = AlertTone.Info, Title = Loc.T("settings.remote.unsupported"), Content = Loc.T("settings.remote.unsupportedDetail") };
            return;
        }
        if (_parts is null)
        {
            _parts = new Parts(this, s);
            _host.Content = _parts.Root;
        }
        _parts.Update(s);
    }

    private bool Remote => _api.Auth.Data?.Remote == true;

    private bool AuthRequired => _api.Auth.Data?.Required == true;

    /// <summary>Everything that exists once the status is known; the groups update in place.</summary>
    private sealed class Parts
    {
        private readonly RemoteAccessView _v;
        public readonly StackPanel Root = new();

        // status
        private readonly SettingsGroup _status;
        private readonly SettingsRow _enableRow;
        private readonly ControlledSwitch _enable = new();
        private readonly TextBlock _stateText = new();
        private readonly TextBlock _percent = new();
        private Control _stateDetail = null!;
        private readonly StatusDot _dot = new();
        private readonly Control _blockerNote, _cfNote, _publicHint, _errorLine;
        private readonly TextBlock _blockerText, _cfNoteText;
        private readonly SettingsRow _publicRow;
        private readonly TextBlock _publicText;
        private readonly DropdownMenu _copyMenu;
        private readonly Alert _error;

        // mode
        private readonly SettingsGroup _mode;
        private readonly SettingsRow _modeRow;
        private readonly ControlledSegmented _modeSwitch;
        private readonly Control _quickWarning;
        private readonly SettingsRow _tokenRow, _hostRow, _originRow;
        private readonly Input _token;
        private readonly Input _hostname;
        private readonly TextBlock _origin;
        private readonly Control _namedBar;
        private readonly ArcButton _saveNamed;
        private string _tokenText = "", _hostText = "";
        private bool _syncing;

        // scope
        private readonly SettingsGroup _scope;
        private readonly SettingsRow _adminRow;
        private readonly ControlledSwitch _admin = new();
        private readonly PasswordRows _password;

        // cloudflared
        private readonly SettingsGroup _cf;
        private readonly SettingsRow _programRow, _sourceRow, _baseRow, _installedRow;
        private readonly Control _programDotHost, _pathLine;
        private StatusDot _programDot = null!;
        private readonly ControlledSelect _source;
        private readonly Input _baseUrl, _path;
        private readonly ArcButton _saveBase, _useDownloaded, _choose, _detect, _usePath;
        private string _baseText = "", _pathText = "";
        private bool _choosing;

        // log
        private readonly Accordion _logBox = new();
        private readonly AccordionItem _logItem = new();
        private readonly SelectableTextBlock _logText = new();
        private readonly TextBlock _logEmpty = new();
        private TunnelStatusDto _s;

        public Parts(RemoteAccessView view, TunnelStatusDto s)
        {
            _v = view;
            _s = s;

            // ---- status
            _enable.ToggleRequested += on => _ = OnToggleAsync(on);
            Avalonia.Automation.AutomationProperties.SetName(_enable, Loc.T("settings.remote.enable"));
            _stateText.VerticalAlignment = VerticalAlignment.Center;
            _stateText.Token(TextBlock.FontSizeProperty, ArcDesignTokenKind.TypeXS).Token(TextBlock.ForegroundProperty, ArcDesignTokenKind.TextSecondary);
            _percent.VerticalAlignment = VerticalAlignment.Center;
            _percent.Token(TextBlock.FontSizeProperty, ArcDesignTokenKind.TypeXS).Token(TextBlock.ForegroundProperty, ArcDesignTokenKind.TextSecondary);
            var detail = Ui.Inline(6, _dot, _stateText, _percent);
            _stateDetail = detail;
            _enableRow = RowBuilder.Row(Loc.T("settings.remote.enable"), detail, _enable);
            _blockerText = Ui.Note("", ArcDesignTokenKind.Warning, 12);
            _blockerNote = Ui.GroupLine(_blockerText);
            _cfNoteText = Ui.Note("", ArcDesignTokenKind.TextMuted, 12);
            _cfNote = Ui.GroupLine(_cfNoteText);
            // web: plain text, so the address can be selected and copied by hand.
            _publicText = new SelectableTextBlock { VerticalAlignment = VerticalAlignment.Center };
            _publicText.Token(TextBlock.FontSizeProperty, ArcDesignTokenKind.TypeXS).Token(TextBlock.ForegroundProperty, ArcDesignTokenKind.TextSecondary);
            _publicText.Token(TextBlock.FontFamilyProperty, ArcDesignTokenKind.MonoFontFamily);
            _publicText.TextTrimming = TextTrimming.CharacterEllipsis;
            _copyMenu = new DropdownMenu
            {
                Trigger = Ui.IconButton(LucideIconKind.Copy, Loc.T("settings.remote.copyMenu"), () => { }),
            };
            _publicRow = RowBuilder.Row(Loc.T("settings.remote.publicUrl"), _publicText, _copyMenu,
                Ui.IconButton(LucideIconKind.ExternalLink, Loc.T("settings.remote.openPage"), () => _ = OpenPageAsync()));
            _publicHint = Ui.GroupNote(Loc.T("settings.remote.publicUrlHint"), ArcDesignTokenKind.TextMuted, 11);
            _error = new Alert();
            _errorLine = new Border { Padding = new Thickness(12, 8), Child = _error };
            _status = RowBuilder.Group(Loc.T("settings.remote.status"), Loc.T("settings.remote.statusFooter"));

            // ---- mode
            _modeSwitch = new ControlledSegmented([("quick", Loc.T("settings.remote.mode.quick")), ("named", Loc.T("settings.remote.mode.named"))], s.Mode);
            Avalonia.Automation.AutomationProperties.SetName(_modeSwitch, Loc.T("settings.remote.modeLabel"));
            _modeSwitch.Chose += m => _ = ChangeModeAsync(m);
            _modeRow = RowBuilder.Row(Loc.T("settings.remote.modeLabel"), null, _modeSwitch);
            _quickWarning = new Border { Padding = new Thickness(12, 8), Child = new Alert { Tone = AlertTone.Warning, Title = Loc.T("settings.remote.quickWarning") } };
            _token = Ui.Secret(Loc.T("settings.remote.token"), "", t => Typed(() => _tokenText = t), 256, null, mono: true);
            _tokenRow = RowBuilder.Row(Loc.T("settings.remote.token"), null, _token);
            _hostname = Ui.Field(Loc.T("settings.remote.hostname"), s.Hostname ?? "", t => Typed(() => _hostText = t), 256, mono: true, placeholder: "astra.example.com");
            _hostText = s.Hostname ?? "";
            _hostRow = RowBuilder.Row(Loc.T("settings.remote.hostname"), Loc.T("settings.remote.hostnameHint"), _hostname);
            _origin = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
            _origin.Token(TextBlock.FontSizeProperty, ArcDesignTokenKind.TypeXS).Token(TextBlock.FontFamilyProperty, ArcDesignTokenKind.MonoFontFamily);
            _originRow = RowBuilder.Row(Loc.T("settings.remote.origin"), Loc.T("settings.remote.originHint"), _origin,
                Ui.IconButton(LucideIconKind.Copy, Loc.T("common.copy"), () => _ = _v.Host.CopyAsync(_s.OriginUrl)));
            _saveNamed = Ui.Button(Loc.T("common.save"), () => _ = SaveNamedAsync());
            var guide = Ui.Button(Loc.T("settings.remote.guide"), () => _ = SystemActions.OpenUrlAsync(_v, TunnelLogic.GuideUrl), ButtonVariant.Ghost, LucideIconKind.BookOpen);
            var barGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(16, 8) };
            barGrid.Children.Add(guide);
            Grid.SetColumn(_saveNamed, 1);
            barGrid.Children.Add(_saveNamed);
            guide.HorizontalAlignment = HorizontalAlignment.Left;
            _namedBar = barGrid;
            _mode = RowBuilder.Group(Loc.T("settings.remote.mode"), null);

            // ---- scope
            _admin.ToggleRequested += on => _ = OnAdminAsync(on);
            Avalonia.Automation.AutomationProperties.SetName(_admin, Loc.T("settings.remote.scope.admin"));
            _adminRow = RowBuilder.Row(Loc.T("settings.remote.scope.admin"), null, _admin);
            _password = new PasswordRows(async pw => await _v.Host.TryAsync(async () =>
            {
                await _v._api.SetAdminPasswordAsync(pw);
                _v.Host.Success(Loc.T("settings.remote.passwordSaved"));
            }));
            _password.RowsChanged += () => Assemble();
            _scope = RowBuilder.Group(Loc.T("settings.remote.scope"), Loc.T("settings.remote.scopeFooter"));

            // ---- cloudflared
            _programDot = new StatusDot();
            _programDotHost = _programDot;
            _programRow = RowBuilder.Row(Loc.T("settings.remote.cf.program"), null, _programDot);
            _source = new ControlledSelect(TunnelLogic.DownloadSources.Select(v => (v, Loc.T("settings.remote.cf.source." + v))), s.Download.DownloadSource, 176);
            Avalonia.Automation.AutomationProperties.SetName(_source, Loc.T("settings.remote.cf.source"));
            _source.Chose += v => _ = ChangeSourceAsync(v);
            _sourceRow = RowBuilder.Row(Loc.T("settings.remote.cf.source"), null, _source);
            _baseUrl = Ui.Field(Loc.T("settings.remote.cf.baseUrl"), s.Download.DownloadBaseUrl ?? "", t => Typed(() => { _baseText = t; UpdateCf(); }), 256, mono: true,
                placeholder: "https://…/cloudflare/cloudflared/releases/download");
            _baseText = s.Download.DownloadBaseUrl ?? "";
            _saveBase = Ui.Button(Loc.T("common.save"), () => _ = SaveCfAsync(new TunnelConfigRequest(null, null, null, null, DownloadSource: "custom", DownloadBaseUrl: _baseText.Trim())));
            _baseRow = RowBuilder.Row(Loc.T("settings.remote.cf.baseUrl"), Loc.T("settings.remote.cf.baseUrlHint"), _baseUrl, _saveBase);
            _useDownloaded = Ui.Button(Loc.T("settings.remote.cf.useDownloaded"), () => _ = SaveCfAsync(new TunnelConfigRequest(null, null, null, null, CloudflaredPath: "")));
            _choose = Ui.Button(Loc.T("settings.remote.cf.choose"), () =>
            {
                _choosing = true;
                _ = FindAsync();
                UpdateCf();
                // web: the path field has autoFocus.
                Avalonia.Threading.Dispatcher.UIThread.Post(() => _path!.Focus(), Avalonia.Threading.DispatcherPriority.Background);
            });
            _installedRow = RowBuilder.Row(Loc.T("settings.remote.cf.installed"), null);
            _path = Ui.Field(Loc.T("settings.remote.cf.installedPath"), "", t => Typed(() => { _pathText = t; UpdateCf(); }), 288, mono: true, placeholder: Loc.T("settings.remote.cf.installedPath"));
            _detect = Ui.Button(Loc.T("settings.remote.cf.detect"), () => _ = FindAsync(), ButtonVariant.Ghost);
            var cancelPath = Ui.Button(Loc.T("common.cancel"), () => { _choosing = false; UpdateCf(); }, ButtonVariant.Ghost);
            _usePath = Ui.Button(Loc.T("settings.remote.cf.use"), () => _ = SaveCfAsync(new TunnelConfigRequest(null, null, null, null, CloudflaredPath: _pathText.Trim()), () => { _choosing = false; }), ButtonVariant.Primary);
            _pathLine = Ui.ButtonBar([_path, _detect, cancelPath, _usePath]);
            _cf = RowBuilder.Group("cloudflared", Loc.T("settings.remote.cf.footer"));

            // ---- log
            _logText.TextWrapping = TextWrapping.Wrap;
            _logText.Token(TextBlock.FontFamilyProperty, ArcDesignTokenKind.MonoFontFamily).Token(TextBlock.ForegroundProperty, ArcDesignTokenKind.TextSecondary);
            _logText.FontSize = 10.5;
            _logEmpty.Text = Loc.T("settings.remote.logEmpty");
            _logEmpty.Token(TextBlock.FontSizeProperty, ArcDesignTokenKind.TypeXS).Token(TextBlock.ForegroundProperty, ArcDesignTokenKind.TextMuted);
            _logItem.Content = new ScrollViewer { MaxHeight = 288, Content = _logText, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
            _logBox.ExpandedIndex = -1;
            _logBox.Items.Add(_logItem);

            Root.Children.Add(_status);
            Root.Children.Add(_mode);
            Root.Children.Add(_scope);
            Root.Children.Add(_cf);
            Root.Children.Add(_logBox);
        }

        private void Typed(Action set)
        {
            if (_syncing) return;
            set();
            UpdateModeState();
        }

        public void Update(TunnelStatusDto s)
        {
            var previous = _s;
            _s = s;
            _syncing = true;
            try
            {
                // Fields take the server's value only when it changed there; what is typed stays.
                if (previous.Hostname != s.Hostname) { _hostText = s.Hostname ?? ""; _hostname.Text = _hostText; }
                if (previous.Download.DownloadBaseUrl != s.Download.DownloadBaseUrl) { _baseText = s.Download.DownloadBaseUrl ?? ""; _baseUrl.Text = _baseText; }
                _token.PlaceholderText = s.HasToken ? "••••••••" : "eyJhIjoi…";
            }
            finally
            {
                _syncing = false;
            }
            UpdateStatus();
            UpdateModeState();
            UpdateScope();
            UpdateCf();
            UpdateLog();
        }

        // ------------------------------------------------------------ status
        private void UpdateStatus()
        {
            var s = _s;
            var why = TunnelLogic.BlockerText(s);
            _enable.SetValue(s.Enabled);
            _enable.IsEnabled = !_v._toggling && !(!s.Enabled && why is not null);
            _dot.Tone = Ui.ToKit(TunnelLogic.Tone(s));
            _stateText.Text = TunnelLogic.StateText(s, _v.Context.Clock.Now, Loc.Language).Text;
            var pct = TunnelLogic.ProgressPercent(s);
            _percent.Text = pct is { } p ? $"{p}%" : "";
            _percent.IsVisible = pct is not null;
            // web: title={formatDateTime(connectedAt)} on the state line.
            ToolTip.SetTip(_stateDetail, s.ConnectedAt is { } at ? SettingsFormat.DateTime(at, Loc.Language) : null);
            var rows = new List<Control> { _enableRow };
            if (!s.Enabled && why is not null)
            {
                _blockerText.Text = why;
                rows.Add(_blockerNote);
            }
            else if (!s.Enabled && TunnelLogic.CloudflaredNote(s) is { } note)
            {
                _cfNoteText.Text = note.Text;
                rows.Add(_cfNote);
            }
            if (s.Enabled && !string.IsNullOrEmpty(s.PublicUrl))
            {
                var baseUrl = s.PublicUrl!;
                _publicText.Text = $"{baseUrl}/v1";
                RebuildCopyMenu(baseUrl);
                rows.Add(_publicRow);
                rows.Add(_publicHint);
            }
            if (TunnelLogic.ShownError(s) is { } raw)
            {
                _error.Tone = TunnelLogic.ErrorIsWarning(s) ? AlertTone.Warning : AlertTone.Danger;
                _error.Title = TunnelLogic.FriendlyError(raw, s.OriginUrl).Text;
                _error.Content = raw;
                rows.Add(_errorLine);
            }
            RowAssembly.Sync(_status, rows);
        }

        private string _menuFor = "";

        private void RebuildCopyMenu(string baseUrl)
        {
            if (_menuFor == baseUrl) return;
            _menuFor = baseUrl;
            _copyMenu.Items.Clear();
            var first = true;
            foreach (var (labelKey, value) in TunnelLogic.CopyChoices(baseUrl))
            {
                var entry = new MenuEntry { Header = Loc.T(labelKey), HasSeparatorBefore = !first && labelKey.EndsWith(".key", StringComparison.Ordinal) };
                var text = value;
                entry.Click += (_, _) => _ = _v.Host.CopyAsync(text);
                _copyMenu.Items.Add(entry);
                first = false;
            }
        }

        private async Task OpenPageAsync()
        {
            if (!string.IsNullOrEmpty(_s.PublicUrl)) await SystemActions.OpenUrlAsync(_v, _s.PublicUrl!);
        }

        private async Task OnToggleAsync(bool on)
        {
            if (on && !_v._ack.Acknowledged)
            {
                if (!await _v.Host.ConfirmAsync(Loc.T("settings.remote.enableConfirm"), Loc.T("settings.remote.enableDetail"), Loc.T("settings.remote.enableAction"))) return;
                _v._ack.Acknowledge();
            }
            if (!on && _v.Remote
                && !await _v.Host.ConfirmAsync(Loc.T("settings.remote.disableSelfConfirm"), Loc.T("settings.remote.disableSelfDetail"), Loc.T("settings.remote.disableAction"), true))
                return;
            _v._toggling = true;
            UpdateStatus();
            await _v.Host.TryAsync(() => _v._api.ToggleTunnelAsync(on));
            _v._toggling = false;
            UpdateStatus();
        }

        // ------------------------------------------------------------ mode
        private void UpdateModeState()
        {
            var s = _s;
            _modeSwitch.SetValue(s.Mode);
            _modeRow.Detail = Loc.T($"settings.remote.mode.{s.Mode}.hint");
            _mode.Footer = s.Mode == "named" ? Loc.T("settings.remote.namedFooter") : null;
            _tokenRow.Detail = Loc.T(s.HasToken ? "settings.remote.tokenSaved" : "settings.remote.tokenHint");
            _origin.Text = s.OriginUrl;
            _saveNamed.IsEnabled = TunnelLogic.ModeFormDirty(s, _tokenText, _hostText) && !_v._updating;
            RowAssembly.Sync(_mode, s.Mode == "quick"
                ? [_modeRow, _quickWarning]
                : [_modeRow, _tokenRow, _hostRow, _originRow, _namedBar]);
        }

        private async Task ChangeModeAsync(string mode)
        {
            if (_s.Enabled)
            {
                // Running: the tunnel restarts (a quick address changes), or stops when the named tunnel isn't filled in yet.
                var stops = TunnelLogic.ModeSwitchStops(_s, mode);
                var ok = await _v.Host.ConfirmAsync(Loc.T("settings.remote.modeSwitchConfirm"),
                    Loc.T(stops ? "settings.remote.modeSwitchStopDetail" : "settings.remote.modeSwitchDetail"), Loc.T("settings.remote.modeSwitchAction"), stops);
                if (!ok)
                {
                    _modeSwitch.SetValue(_s.Mode);
                    return;
                }
            }
            if (await _v.Host.TryAsync(() => _v._api.PutTunnelAsync(new TunnelConfigRequest(mode, null, null, null))) is null) _modeSwitch.SetValue(_s.Mode);
        }

        private async Task SaveNamedAsync()
        {
            _v._updating = true;
            _saveNamed.IsLoading = true;
            var next = await _v.Host.TryAsync(() => _v._api.PutTunnelAsync(TunnelLogic.ModeFormRequest(_tokenText, _hostText)));
            _v._updating = false;
            _saveNamed.IsLoading = false;
            if (next is not null)
            {
                _v.Host.Success(Loc.T(next.Enabled ? "settings.remote.savedRestart" : "settings.remote.saved"));
                _syncing = true;
                _tokenText = "";
                _token.Text = "";
                _syncing = false;
            }
            UpdateModeState();
        }

        // ------------------------------------------------------------ scope
        private void UpdateScope()
        {
            var s = _s;
            _admin.SetValue(s.AllowAdmin);
            _admin.IsEnabled = !_v._updating && !(!s.AllowAdmin && !s.Checks.PasswordSet);
            _adminRow.Detail = Loc.T(s.Checks.PasswordSet ? "settings.remote.scope.adminHint" : "settings.remote.block.password");
            _password.Update(s.Checks.PasswordSet, hideChange: _v.AuthRequired);
            Assemble();
        }

        private SettingsRow? _gatewayRow;

        private void Assemble()
        {
            _gatewayRow ??= RowBuilder.Row(Loc.T("settings.remote.scope.gateway"), Loc.T("settings.remote.scope.gatewayHint"), Ui.Badge(Loc.T("settings.remote.scope.gatewayBadge"), "green"));
            RowAssembly.Sync(_scope, RowAssembly.Flatten(_gatewayRow, _adminRow, _password));
        }

        private async Task OnAdminAsync(bool allow)
        {
            if (allow && !await _v.Host.ConfirmAsync(Loc.T("settings.remote.adminConfirm"), Loc.T("settings.remote.adminDetail"), Loc.T("settings.remote.adminAction"), true)) return;
            if (!allow && _v.Remote
                && !await _v.Host.ConfirmAsync(Loc.T("settings.remote.adminOffSelfConfirm"), Loc.T("settings.remote.adminOffSelfDetail"), Loc.T("settings.remote.disableAction"), true))
                return;
            _v._updating = true;
            UpdateScope();
            await _v.Host.TryAsync(() => _v._api.PutTunnelAsync(new TunnelConfigRequest(null, null, null, allow)));
            _v._updating = false;
            UpdateScope();
        }

        // ------------------------------------------------------------ cloudflared
        private void UpdateCf()
        {
            var s = _s;
            var cf = s.Cloudflared;
            if (_pickedSource is null && _source.SelectedValue as string != s.Download.DownloadSource) _source.SetValue(s.Download.DownloadSource);
            _programRow.Detail = TunnelLogic.Program(cf).Text;
            _programDot.Tone = Ui.ToKit(TunnelLogic.ProgramTone(cf));
            if (!string.IsNullOrEmpty(cf.Path)) ToolTip.SetTip(_programRow, cf.Path);
            var shownSource = _pickedSource ?? s.Download.DownloadSource;
            _sourceRow.Detail = Loc.T($"settings.remote.cf.source.{shownSource}.hint");
            _saveBase.IsEnabled = TunnelLogic.CustomSourceSavable(s, _baseText, _v._updating);

            var rows = new List<Control> { _programRow };
            if (string.IsNullOrEmpty(s.Download.CloudflaredPath) && !cf.FromEnvironment)
            {
                rows.Add(_sourceRow);
                if (shownSource == "custom") rows.Add(_baseRow);
            }
            if (!_v.Remote && !cf.FromEnvironment)
            {
                var installedPath = s.Download.CloudflaredPath;
                _installedRow.Detail = !string.IsNullOrEmpty(installedPath) ? MonoText(installedPath) : Loc.T("settings.remote.cf.installedHint");
                _installedRow.Trailing.Clear();
                if (!string.IsNullOrEmpty(installedPath)) { _useDownloaded.IsEnabled = !_v._updating; _installedRow.Trailing.Add(_useDownloaded); }
                else if (!_choosing) _installedRow.Trailing.Add(_choose);
                rows.Add(_installedRow);
            }
            if (_choosing && string.IsNullOrEmpty(s.Download.CloudflaredPath))
            {
                _usePath.IsEnabled = _pathText.Trim() != "" && !_v._updating;
                _usePath.IsLoading = _v._updating;
                rows.Add(_pathLine);
            }
            RowAssembly.Sync(_cf, rows);
        }

        private static Control MonoText(string text)
        {
            var block = KitStyle.Text(text, ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextSecondary);
            block.Token(TextBlock.FontFamilyProperty, ArcDesignTokenKind.MonoFontFamily);
            return block;
        }

        private string? _pickedSource;

        private async Task ChangeSourceAsync(string next)
        {
            // Picking "custom" shows the address field first; it is saved together with the address.
            _pickedSource = next;
            UpdateCf();
            if (next != "custom" || !string.IsNullOrEmpty(_s.Download.DownloadBaseUrl))
            {
                await SaveCfAsync(new TunnelConfigRequest(null, null, null, null, DownloadSource: next));
                _pickedSource = null;
                UpdateCf();
            }
        }

        private async Task SaveCfAsync(TunnelConfigRequest config, Action? after = null)
        {
            _v._updating = true;
            UpdateCf();
            var saved = await _v.Host.TryAsync(() => _v._api.PutTunnelAsync(config));
            _v._updating = false;
            if (saved is not null)
            {
                _v.Host.Success(Loc.T("settings.remote.cf.saved"));
                after?.Invoke();
            }
            else _source.SetValue(_pickedSource ?? _s.Download.DownloadSource);
            UpdateCf();
        }

        private async Task FindAsync()
        {
            _detect.IsLoading = true;
            try
            {
                var found = await _v._api.DetectCloudflaredAsync();
                if (!string.IsNullOrEmpty(found.Path))
                {
                    _pathText = found.Path;
                    _syncing = true;
                    _path.Text = found.Path;
                    _syncing = false;
                    UpdateCf();
                }
                else _v.Host.Success(Loc.T("settings.remote.cf.detectNone"));
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _v.Host.Error(UiHost.ErrorText(e));
            }
            finally
            {
                _detect.IsLoading = false;
            }
        }

        // ------------------------------------------------------------ log
        private void UpdateLog()
        {
            var s = _s;
            _logItem.Header = TunnelLogic.LogTitle(s).Text;
            if (s.Log.Count == 0)
            {
                _logItem.Content = _logEmpty;
            }
            else
            {
                _logText.Text = string.Join('\n', s.Log);
                if (_logItem.Content is not ScrollViewer) _logItem.Content = new ScrollViewer { MaxHeight = 288, Content = _logText, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
            }
        }
    }
}
