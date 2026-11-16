using System.Text;
using Astra.Api.Contracts;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Components.Settings;
using Astra.Desktop.Core.Backup;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Shell;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using TKCode.Arc;
using ArcButton = TKCode.Arc.Button;
using TKCode.Arc.Icons.Lucide;
using SettingsGroup = Astra.Desktop.Components.Kit.SettingsGroup;
using SettingsRow = Astra.Desktop.Components.Kit.SettingsRow;

namespace Astra.Desktop.Pages.Settings;

/// <summary>
/// Settings › Backup &amp; sync (web <c>BackupSyncSettings</c>, linked update 38), compact: a whole-installation backup file, restore from a file or
/// an automatic backup with a per-section preview, the automatic backup schedule and its files, and the multi-machine sync target. Everything but
/// the sync status is local-origin only on the server: a refusal reaches the user as the error toast. The forms are drawn once and only their
/// status parts move on a refresh, so a passphrase being typed survives the polls. The section picker is not mirrored: backups and syncs carry
/// every default section. Rules: <see cref="BackupLogic"/>.
/// </summary>
public sealed class BackupSyncView : LiveView
{
    private static readonly FilePickerFileType BackupFile = new("Astra backup") { Patterns = ["*.astra-backup", "*.json"] };

    private readonly BackupApi _api;

    // backup
    private bool _includeKeys;
    private string _exportPass = "";
    private ArcButton? _exportButton;

    // restore
    private string? _fileContent;
    private string? _fileName;
    private string? _autoName;
    private string _restorePass = "";
    private string _mode = "merge";
    private readonly StackPanel _restoreOut = new() { Spacing = 4 };
    private SettingsRow _sourceRow = null!;

    // automatic
    private ControlledSwitch _autoEnabled = null!;
    private DeferredNumberField _hours = null!, _keep = null!;
    private string _autoPass = "";
    private SettingsGroup _autoGroup = null!;
    private readonly StackPanel _autoFiles = new();
    private SettingsRow _autoPassRow = null!;

    // sync
    private string _kind = "folder", _url = "", _user = "", _secret = "", _folder = "", _bucket = "", _region = "", _branch = "", _syncPass = "";
    private bool _pathStyle, _syncKeys, _syncSignins;
    private int _interval = BackupLogic.DefaultIntervalMinutes;
    private bool _formLoaded, _lastConfigured;
    private ControlledSelect _kindSelect = null!;
    private ControlledSwitch _syncEnabled = null!, _keysSwitch = null!, _signinsSwitch = null!, _pathStyleSwitch = null!;
    private DeferredNumberField _intervalField = null!;
    private Input _urlBox = null!, _userBox = null!, _secretBox = null!, _folderBox = null!, _bucketBox = null!, _regionBox = null!, _branchBox = null!;
    private SettingsRow _urlRow = null!, _userRow = null!, _secretRow = null!, _bucketRow = null!, _regionRow = null!, _pathRow = null!, _branchRow = null!, _folderRow = null!;
    private readonly List<ArcButton> _needsTarget = [];
    private ArcButton _saveSync = null!;
    private SettingsGroup _syncGroup = null!;
    private readonly StackPanel _syncOut = new() { Spacing = 4 };
    private readonly StackPanel _syncStatus = new();
    private SyncStatusDto? _status;

    /// <summary>Creates the view over the app context.</summary>
    public BackupSyncView(PageContext context) : base(context)
    {
        _api = new BackupApi(context.Api, context.Queries);
        Watch(_api.Auto);
        Watch(_api.SyncStatus);
    }

    /// <inheritdoc />
    protected override void Build()
    {
        _needsTarget.Clear();
        _formLoaded = false;
        var root = new StackPanel { Spacing = 0 };
        root.Children.Add(BuildBackup());
        root.Children.Add(BuildRestore());
        root.Children.Add(BuildAuto());
        root.Children.Add(BuildSync());
        Content = root;
    }

    private static ControlledSwitch Switch(string name, Action<bool> on)
    {
        var s = new ControlledSwitch();
        Avalonia.Automation.AutomationProperties.SetName(s, name);
        s.ToggleRequested += v => { s.SetValue(v); on(v); };
        return s;
    }

    private static Control Lines(StackPanel panel) => new Border { Padding = new Avalonia.Thickness(16, 4, 16, 8), Child = panel };

    // ----- backup -----

    private SettingsGroup BuildBackup()
    {
        var keys = Switch(Loc.T("backup.all.export.keys"), v => { _includeKeys = v; UpdateExport(); });
        keys.SetValue(_includeKeys);
        var pass = Ui.Secret(Loc.T("backup.all.passphrase"), _exportPass, t => { _exportPass = t; UpdateExport(); }, 240, Loc.T("backup.all.passphrase"));
        _exportButton = Ui.Button(Loc.T("backup.all.export.run"), () => _ = ExportAsync(), ButtonVariant.Primary, LucideIconKind.Download);
        UpdateExport();
        return RowBuilder.Group(Loc.T("backup.all.export.title"), Loc.T("backup.all.export.noKeys"),
            Ui.GroupNote(Loc.T("backup.all.export.hint")),
            RowBuilder.Row(Loc.T("backup.all.export.keys"), Loc.T("backup.all.localOnly.keys"), keys),
            RowBuilder.Row(Loc.T("backup.all.passphrase"), Loc.T("backup.all.passphrase.warn"), pass),
            Ui.GroupLine(_exportButton));
    }

    private void UpdateExport()
    {
        if (_exportButton is not null) _exportButton.IsEnabled = BackupLogic.CanExport(_includeKeys, _exportPass);
    }

    private async Task ExportAsync()
    {
        var file = await Host.TryAsync(() => _api.ExportAsync(BackupLogic.ExportInput(_includeKeys, _exportPass)));
        if (file is null) return;
        if (!await SaveAsync(file.Name, file.Content)) return;
        Context.Feedback.Success(Loc.T("backup.all.export.done", ("count", file.Documents)));
    }

    private async Task<bool> SaveAsync(string name, string text)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return false;
        var target = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = name, DefaultExtension = "astra-backup", FileTypeChoices = [BackupFile], ShowOverwritePrompt = true,
        });
        if (target is null) return false;
        await using var stream = await target.OpenWriteAsync();
        stream.SetLength(0);
        await stream.WriteAsync(new UTF8Encoding(false).GetBytes(text));
        return true;
    }

    // ----- restore -----

    private SettingsGroup BuildRestore()
    {
        var pick = Ui.Button(Loc.T("backup.all.restore.choose"), () => _ = PickAsync(), icon: LucideIconKind.FolderOpen);
        _sourceRow = RowBuilder.Row(Loc.T("backup.all.restore.source.file"), SourceText(), pick);
        var pass = Ui.Secret(Loc.T("backup.all.restore.passphrase"), _restorePass, t => _restorePass = t, 240, Loc.T("backup.all.restore.passphrase"));
        var mode = new ControlledSegmented([("merge", Loc.T("backup.all.restore.mode.merge")), ("replace", Loc.T("backup.all.restore.mode.replace"))], _mode);
        mode.Chose += v => { _mode = v; mode.SetValue(v); ClearPlan(); };
        var preview = Ui.Button(Loc.T("backup.all.restore.preview"), () => _ = PreviewAsync());
        var run = Ui.Button(Loc.T("backup.all.restore.run"), () => _ = RestoreAsync(), ButtonVariant.Primary);
        return RowBuilder.Group(Loc.T("backup.all.restore.title"), Loc.T(_mode == "replace" ? "backup.all.restore.mode.replaceHint" : "backup.all.restore.mode.mergeHint"),
            _sourceRow,
            RowBuilder.Row(Loc.T("backup.all.restore.passphrase"), null, pass),
            RowBuilder.Row(Loc.T("backup.all.restore.mode"), null, mode),
            Ui.GroupLine(Ui.Inline(8, preview, run)),
            Lines(_restoreOut));
    }

    private string? SourceText() => _fileName ?? (_autoName is null ? null : Loc.T("backup.all.restore.source.auto") + ": " + _autoName);

    private void ClearPlan()
    {
        _restoreOut.Children.Clear();
    }

    private async Task PickAsync()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = false, FileTypeFilter = [BackupFile] });
        if (files.Count == 0) return;
        try
        {
            await using var stream = await files[0].OpenReadAsync();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            _fileContent = await reader.ReadToEndAsync();
            _fileName = files[0].Name;
            _autoName = null;
            _sourceRow.Detail = SourceText();
            ClearPlan();
        }
        catch (IOException e) { Context.Feedback.Error(e.Message); }
    }

    private BackupSourceInput? Source() =>
        _autoName is { } a ? BackupLogic.AutoSource(a, _restorePass, _mode)
        : _fileContent is { } c ? BackupLogic.FileSource(c, _restorePass, _mode)
        : null;

    private void ShowLines(IEnumerable<string> lines)
    {
        _restoreOut.Children.Clear();
        foreach (var l in lines) _restoreOut.Children.Add(Ui.Note(l, TKCode.Arc.DesignTokens.ArcDesignTokenKind.TextSecondary));
    }

    private async Task PreviewAsync()
    {
        if (Source() is not { } src) { Context.Feedback.Error(Loc.T("backup.all.restore.choose")); return; }
        var plan = await Host.TryAsync(() => _api.PreviewAsync(src));
        if (plan is null) return;
        ShowLines(BackupLogic.PlanLines(plan));
    }

    private async Task RestoreAsync()
    {
        if (Source() is not { } src) { Context.Feedback.Error(Loc.T("backup.all.restore.choose")); return; }
        if (_mode == "replace" && !await Host.ConfirmAsync(Loc.T("backup.all.restore.replaceConfirm"), Loc.T("backup.all.restore.replaceDetail"), Loc.T("backup.all.restore.run"), destructive: true)) return;
        var result = await Host.TryAsync(() => _api.RestoreAsync(src));
        if (result is null) return;
        ShowLines(BackupLogic.RestoreLines(result));
        Context.Feedback.Success(Loc.T("backup.all.restore.done", ("written", result.Written), ("removed", result.Removed)));
    }

    // ----- automatic backups -----

    private SettingsGroup BuildAuto()
    {
        _autoEnabled = Switch(Loc.T("backup.all.auto.enabled"), v => _ = Host.TryAsync(() => _api.SetAutoAsync(new AutoBackupInput(v, null, null, null, null))));
        _hours = new DeferredNumberField(Loc.T("backup.all.auto.every"), _api.Auto.Data?.Hours ?? 24, v => _ = Host.TryAsync(() => _api.SetAutoAsync(new AutoBackupInput(null, (int)v, null, null, null))), " " + Loc.T("backup.all.auto.hours"));
        _keep = new DeferredNumberField(Loc.T("backup.all.auto.keep"), _api.Auto.Data?.Keep ?? 10, v => _ = Host.TryAsync(() => _api.SetAutoAsync(new AutoBackupInput(null, null, (int)v, null, null))));
        var pass = Ui.Secret(Loc.T("backup.all.auto.passphrase"), _autoPass, t => _autoPass = t, 200, Loc.T("backup.all.auto.passphrase"));
        var set = Ui.Button(Loc.T("backup.all.auto.setPassphrase"), () => _ = SetAutoPassAsync());
        var clear = Ui.Button(Loc.T("backup.all.auto.clearPassphrase"), () => _ = Host.TryAsync(() => _api.SetAutoAsync(new AutoBackupInput(null, null, null, null, true))), ButtonVariant.Ghost);
        _autoPassRow = RowBuilder.Row(Loc.T("backup.all.auto.passphrase"), Loc.T("backup.all.auto.passphraseHint"), pass, set, clear);
        var run = Ui.Button(Loc.T("backup.all.auto.run"), () => _ = RunAutoAsync(), icon: LucideIconKind.Archive);
        _autoGroup = RowBuilder.Group(Loc.T("backup.all.auto.title"), null,
            RowBuilder.Row(Loc.T("backup.all.auto.enabled"), null, _autoEnabled),
            RowBuilder.Row(Loc.T("backup.all.auto.every"), null, _hours),
            RowBuilder.Row(Loc.T("backup.all.auto.keep"), null, _keep),
            _autoPassRow,
            Ui.GroupLine(run),
            _autoFiles);
        return _autoGroup;
    }

    private async Task SetAutoPassAsync()
    {
        if (BackupLogic.PassphraseTooShort(_autoPass) || _autoPass.Length == 0)
        {
            Context.Feedback.Error(Loc.T("backup.all.passphrase.short", ("min", BackupLogic.MinPassphrase)));
            return;
        }
        if (await Host.TryAsync(() => _api.SetAutoAsync(new AutoBackupInput(null, null, null, _autoPass, null))) is not null) _autoPass = "";
    }

    private async Task RunAutoAsync()
    {
        if (await Host.TryAsync(() => _api.RunAutoAsync()) is not null) Context.Feedback.Success(Loc.T("backup.all.auto.ran"));
    }

    private void RefreshAuto()
    {
        _autoFiles.Children.Clear();
        if (_api.Auto.Data is not { } a)
        {
            if (_api.Auto.IsError) _autoFiles.Children.Add(Ui.GroupLine(Ui.ErrorText(UiHost.ErrorText(_api.Auto.Error!))));
            return;
        }
        _autoEnabled.SetValue(a.Enabled);
        _hours.SetSaved(a.Hours);
        _keep.SetSaved(a.Keep);
        _autoPassRow.Detail = (a.HasPassphrase ? Loc.T("backup.all.auto.passphraseSet") + " · " : "") + Loc.T("backup.all.auto.passphraseHint");
        _autoGroup.Footer = Loc.T("backup.all.auto.hint", ("dir", a.Dir));
        if (a.Files.Count == 0) _autoFiles.Children.Add(Ui.GroupNote(Loc.T("backup.all.auto.empty")));
        foreach (var f in a.Files)
        {
            var file = f;
            var detail = $"{Loc.T("backup.all.auto.kind." + f.Kind)} · {BackupLogic.FormatTime(f.At.ToString("O"))} · {BackupLogic.FormatSize(f.Size)}"
                + (f.Encrypted ? " · " + Loc.T("backup.all.auto.encrypted") : "");
            var restore = Ui.Button(Loc.T("backup.all.auto.restoreFrom"), () => UseAuto(file.Name), ButtonVariant.Ghost);
            var delete = Ui.IconButton(LucideIconKind.Trash2, Loc.T("common.delete"), () => _ = DeleteAutoAsync(file.Name));
            _autoFiles.Children.Add(RowBuilder.Row(f.Name, detail, restore, delete));
        }
    }

    private void UseAuto(string name)
    {
        _autoName = name;
        _fileContent = null;
        _fileName = null;
        _sourceRow.Detail = SourceText();
        ClearPlan();
        Context.Feedback.Success(Loc.T("backup.all.restore.source.auto") + ": " + name);
    }

    private async Task DeleteAutoAsync(string name)
    {
        if (!await Host.ConfirmAsync(Loc.T("backup.all.auto.deleteConfirm", ("name", name)), null, Loc.T("common.delete"), destructive: true)) return;
        if (await Host.TryAsync(() => _api.DeleteAutoAsync(name))) { if (_autoName == name) { _autoName = null; _sourceRow.Detail = SourceText(); } }
    }

    // ----- sync -----

    private SettingsGroup BuildSync()
    {
        _kindSelect = new ControlledSelect(BackupLogic.Kinds.Select(k => (k, Loc.T("sync.kind." + k))), _kind, 200);
        Avalonia.Automation.AutomationProperties.SetName(_kindSelect, Loc.T("sync.kind"));
        _kindSelect.Chose += k => { _kind = k; _kindSelect.SetValue(k); UpdateKind(); };

        _urlBox = Ui.Field(Loc.T("sync.url.webdav"), _url, t => { _url = t; UpdateKind(); }, 280);
        _userBox = Ui.Field(Loc.T("sync.username"), _user, t => _user = t, 220);
        _secretBox = Ui.Secret(Loc.T("sync.secret"), _secret, t => _secret = t, 220);
        _bucketBox = Ui.Field(Loc.T("sync.bucket"), _bucket, t => { _bucket = t; UpdateKind(); }, 220, placeholder: Loc.T("sync.bucket"));
        _regionBox = Ui.Field(Loc.T("sync.region"), _region, t => _region = t, 160, placeholder: Loc.T("sync.region"));
        _branchBox = Ui.Field(Loc.T("sync.branch"), _branch, t => _branch = t, 220, placeholder: Loc.T("sync.branch"));
        _folderBox = Ui.Field(Loc.T("sync.folder"), _folder, t => { _folder = t; UpdateKind(); }, 280);
        _pathStyleSwitch = Switch(Loc.T("sync.pathStyle"), v => _pathStyle = v);
        _pathStyleSwitch.SetValue(_pathStyle);

        _urlRow = RowBuilder.Row(Loc.T("sync.url.webdav"), null, _urlBox);
        _userRow = RowBuilder.Row(Loc.T("sync.username"), null, _userBox);
        _secretRow = RowBuilder.Row(Loc.T("sync.secret"), null, _secretBox);
        _bucketRow = RowBuilder.Row(Loc.T("sync.bucket"), null, _bucketBox);
        _regionRow = RowBuilder.Row(Loc.T("sync.region"), null, _regionBox);
        _pathRow = RowBuilder.Row(Loc.T("sync.pathStyle"), null, _pathStyleSwitch);
        _branchRow = RowBuilder.Row(Loc.T("sync.branch"), Loc.T("sync.github.private"), _branchBox);
        _folderRow = RowBuilder.Row(Loc.T("sync.folder"), null, _folderBox);

        var pass = Ui.Secret(Loc.T("sync.passphrase"), _syncPass, t => { _syncPass = t; UpdateKind(); }, 240);
        var passRow = RowBuilder.Row(Loc.T("sync.passphrase"), Loc.T("sync.passphrase.warn"), pass);
        _intervalField = new DeferredNumberField(Loc.T("sync.interval"), _interval, v => _interval = BackupLogic.Interval(v), " " + Loc.T("sync.minutes"), delay: TimeSpan.FromMilliseconds(100)) { Minimum = 1 };
        _signinsSwitch = Switch(Loc.T("sync.includeSignins"), v => _syncSignins = v);
        _keysSwitch = Switch(Loc.T("sync.includeKeys"), v => _syncKeys = v);
        _signinsSwitch.SetValue(_syncSignins);
        _keysSwitch.SetValue(_syncKeys);

        var test = Ui.Button(Loc.T("sync.test"), () => _ = SaveAndAsync(() => TestAsync()));
        _saveSync = Ui.Button(Loc.T("common.save"), () => _ = SaveAndAsync(() => { Context.Feedback.Success(Loc.T("sync.saved")); return Task.CompletedTask; }), ButtonVariant.Primary);
        var now = Ui.Button(Loc.T("sync.now"), () => _ = RunAsync(false));
        var dry = Ui.Button(Loc.T("sync.preview"), () => _ = RunAsync(true));
        var forget = Ui.Button(Loc.T("sync.forget"), () => _ = ForgetAsync(), ButtonVariant.Ghost);
        _needsTarget.AddRange([now, dry, forget]);
        _syncEnabled = Switch(Loc.T("sync.enabled"), v => _ = Host.TryAsync(() => _api.SetSyncAsync(new SyncConfigInput(null, null, null, null, null, null, v))));

        _syncGroup = RowBuilder.Group(Loc.T("sync.title"), Loc.T("sync.hint"),
            RowBuilder.Row(Loc.T("sync.kind"), null, _kindSelect),
            _urlRow, _bucketRow, _regionRow, _userRow, _secretRow, _pathRow, _branchRow, _folderRow, passRow,
            RowBuilder.Row(Loc.T("sync.interval"), null, _intervalField),
            RowBuilder.Row(Loc.T("sync.includeSignins"), null, _signinsSwitch),
            RowBuilder.Row(Loc.T("sync.includeKeys"), Loc.T("sync.includeKeys.hint"), _keysSwitch),
            Ui.GroupLine(Ui.Inline(8, test, _saveSync, now, dry, forget)),
            RowBuilder.Row(Loc.T("sync.enabled"), null, _syncEnabled),
            _syncStatus,
            Lines(_syncOut));
        UpdateKind();
        return _syncGroup;
    }

    /// <summary>Shows the fields the kind uses, and enables the buttons the form (and the saved state) allows.</summary>
    private void UpdateKind()
    {
        var remote = _kind is "webdav" or "s3" or "github";
        _urlRow.IsVisible = remote;
        _urlRow.Label = Loc.T(remote ? "sync.url." + _kind : "sync.url.webdav");
        _urlBox.PlaceholderText = _urlRow.Label as string;
        _userRow.IsVisible = _kind is "webdav" or "s3";
        _userRow.Label = Loc.T(_kind == "s3" ? "sync.accessKey" : "sync.username");
        _secretRow.IsVisible = _kind != "folder";
        _secretRow.Label = Loc.T("sync.secret");
        _secretBox.PlaceholderText = _status is { Target.HasSecret: true } s && s.Target.Kind == _kind ? Loc.T("sync.secret.saved") : null;
        _bucketRow.IsVisible = _regionRow.IsVisible = _pathRow.IsVisible = _kind == "s3";
        _branchRow.IsVisible = _kind == "github";
        _folderRow.Label = Loc.T("sync.folder." + _kind);
        var missing = BackupLogic.MissingTargetField(_kind, _url, _folder, _bucket);
        var passOk = BackupLogic.CanSaveSync(_syncPass, _status?.HasPassphrase == true);
        _saveSync.IsEnabled = missing is null && passOk;
        var configured = _status?.Configured == true;
        foreach (var b in _needsTarget) b.IsEnabled = configured;
    }

    private SyncConfigInput Config() => BackupLogic.SyncConfig(
        BackupLogic.Target(_kind, _url, _user, _secret, _folder, _bucket, _region, _pathStyle, _branch), _syncPass, _interval, _syncKeys, _syncSignins);

    private async Task SaveAndAsync(Func<Task> then)
    {
        if (await Host.TryAsync(() => _api.SetSyncAsync(Config())) is null) return;
        _secret = "";
        _syncPass = "";
        _secretBox.Text = "";
        await then();
    }

    private async Task TestAsync()
    {
        if (await Host.TryAsync(() => _api.TestSyncAsync())) Context.Feedback.Success(Loc.T("sync.test.ok"));
    }

    private async Task RunAsync(bool dry)
    {
        _syncOut.Children.Clear();
        var run = await Host.TryAsync(() => _api.SyncNowAsync(dry));
        if (run is null) return;
        var lines = new List<string> { BackupLogic.RunText(run) };
        lines.AddRange(run.Items.Take(20).Select(i => $"{i.Status}: {i.Label}"));
        lines.AddRange(run.Conflicts.Select(BackupLogic.ConflictLine));
        lines.AddRange(run.Errors);
        foreach (var l in lines) _syncOut.Children.Add(Ui.Note(l, TKCode.Arc.DesignTokens.ArcDesignTokenKind.TextSecondary));
        if (!dry && run.Errors.Count == 0) Context.Feedback.Success(BackupLogic.RunText(run));
    }

    private async Task ForgetAsync()
    {
        if (!await Host.ConfirmAsync(Loc.T("sync.forget.confirm"), Loc.T("sync.forget.detail"), Loc.T("sync.forget"), destructive: true)) return;
        if (await Host.TryAsync(() => _api.ForgetSyncAsync()) is not null) _syncOut.Children.Clear();
    }

    private void RefreshSync()
    {
        if (_api.SyncStatus.Data is not { } s) return;
        _status = s;
        // load the saved target into the form once, and again after Forget (configured flips)
        if (!_formLoaded || _lastConfigured != s.Configured)
        {
            _formLoaded = true;
            _lastConfigured = s.Configured;
            _kind = string.IsNullOrEmpty(s.Target.Kind) ? "folder" : s.Target.Kind;
            _kindSelect.SetValue(_kind);
            _urlBox.Text = _url = s.Target.Url;
            _userBox.Text = _user = s.Target.Username;
            _folderBox.Text = _folder = s.Target.Folder;
            _bucketBox.Text = _bucket = s.Target.Bucket;
            _regionBox.Text = _region = s.Target.Region;
            _branchBox.Text = _branch = s.Target.Branch;
            _pathStyleSwitch.SetValue(_pathStyle = s.Target.PathStyle);
            _interval = s.IntervalMinutes > 0 ? s.IntervalMinutes : BackupLogic.DefaultIntervalMinutes;
            _intervalField.SetSaved(_interval);
            _keysSwitch.SetValue(_syncKeys = s.IncludeKeys);
            _signinsSwitch.SetValue(_syncSignins = s.IncludeSignins);
        }
        _syncEnabled.SetValue(s.Enabled);
        _syncStatus.Children.Clear();
        _syncStatus.Children.Add(Ui.GroupNote(s.Configured ? BackupLogic.LastSyncText(s) + " · " + BackupLogic.CountsText(s) : Loc.T("sync.notConfigured")));
        if (!string.IsNullOrEmpty(s.LastError)) _syncStatus.Children.Add(Ui.GroupAlert(AlertTone.Danger, Loc.T("sync.status.error"), s.LastError));
        if (s.Conflicts.Count > 0)
        {
            var box = new StackPanel { Spacing = 4 };
            box.Children.Add(Ui.Inline(8, Ui.Note(Loc.T("sync.conflicts", ("count", s.Conflicts.Count))),
                Ui.Button(Loc.T("sync.conflicts.clear"), () => _ = Host.TryAsync(() => _api.ClearConflictsAsync()), ButtonVariant.Ghost)));
            foreach (var c in s.Conflicts.Take(10)) box.Children.Add(Ui.Note(BackupLogic.ConflictLine(c), TKCode.Arc.DesignTokens.ArcDesignTokenKind.TextSecondary));
            _syncStatus.Children.Add(Ui.GroupLine(box));
        }
        _syncStatus.Children.Add(Ui.GroupNote(Loc.T("sync.status.device") + ": " + s.DeviceId));
        UpdateKind();
    }

    /// <inheritdoc />
    protected override void Refresh()
    {
        RefreshAuto();
        RefreshSync();
    }
}
