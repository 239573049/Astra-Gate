using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Components.ProviderDetail;
using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Providers;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Icons.Lucide;
using ArcButton = TKCode.Arc.Button;

namespace Astra.Desktop.Components.Providers;

/// <summary>Where the backup sheet reads and writes its file: the OS file dialogs in the app, a stand-in in tests.</summary>
internal interface IBackupFiles
{
    /// <summary>The text of the file the user picked, or null when they cancelled.</summary>
    Task<string?> ReadAsync();

    /// <summary>Saves <paramref name="text"/> under a name the user confirms; false when they cancelled.</summary>
    Task<bool> SaveAsync(string suggestedName, string text);
}

/// <summary>The OS open / save dialogs of the window that holds <paramref name="owner"/>.</summary>
internal sealed class StorageBackupFiles(Control owner) : IBackupFiles
{
    private static readonly FilePickerFileType Json = new("JSON") { Patterns = ["*.json"], MimeTypes = ["application/json"] };

    /// <inheritdoc />
    public async Task<string?> ReadAsync()
    {
        if (TopLevel.GetTopLevel(owner)?.StorageProvider is not { } storage) return null;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = false, FileTypeFilter = [Json] });
        if (files.Count == 0) return null;
        await using var stream = await files[0].OpenReadAsync();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    /// <inheritdoc />
    public async Task<bool> SaveAsync(string suggestedName, string text)
    {
        if (TopLevel.GetTopLevel(owner)?.StorageProvider is not { } storage) return false;
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = suggestedName, DefaultExtension = "json", FileTypeChoices = [Json], ShowOverwritePrompt = true,
        });
        if (file is null) return false;
        await using var stream = await file.OpenWriteAsync();
        stream.SetLength(0);
        await stream.WriteAsync(new UTF8Encoding(false).GetBytes(text));
        return true;
    }
}

/// <summary>
/// "Backup and restore" (web <c>ProviderBackupSheet</c>, issue #33, <c>.plan/provider-backup.md</c>): export providers and their models to one JSON
/// file, or restore from such a file. A file with keys is only made on this machine (the server refuses it elsewhere); previews show masks, never
/// keys. Rules in <see cref="ProviderBackupLogic"/>.
/// </summary>
internal sealed class ProviderBackupSheet : UserControl
{
    private readonly IProviderSheetHost _host;
    private readonly IBackupFiles _files;
    private readonly Sheet _sheet = new() { SheetWidth = 640, Size = ControlSize.Large };
    private readonly StackPanel _exportExtras = new() { Spacing = 12 };
    private readonly StackPanel _importArea = new() { Spacing = 12 };
    private readonly HashSet<int> _ticked = [];
    private string _mode = ProviderBackupLogic.None;
    private string _exportPass = "";
    private string? _exportError;
    private JsonObject? _file;
    private string _importPass = "";
    private BackupPreview? _preview;
    private string? _importError;
    private bool _busy;

    // WebDAV copy (issue #41): the form's inputs are built once (typing must not rebuild them); what follows "configured" is rebuilt.
    private readonly StackPanel _davArea = new() { Spacing = 12 };
    private Input? _davUrlBox;
    private Input? _davUserBox;
    private PasswordField? _davPassBox;
    private Input? _davFolderBox;
    private ArcButton? _davSave;
    private ArcButton? _davTest;
    private ArcButton? _davForget;
    private ArcButton? _davUpload;
    private WebDavStatus _davStatus = new(false, "", "", false, ProviderBackupLogic.DefaultWebDavFolder);
    private string _davUrl = "";
    private string _davUser = "";
    private string _davPass = "";
    private string _davFolder = ProviderBackupLogic.DefaultWebDavFolder;
    private string _davMode = ProviderBackupLogic.None;
    private string _davPassphrase = "";
    private string? _davError;
    private bool _davBusy;

    /// <summary>Builds the (closed) sheet; <paramref name="files"/> null uses the OS dialogs.</summary>
    public ProviderBackupSheet(IProviderSheetHost host, IBackupFiles? files = null)
    {
        _host = host;
        _files = files ?? new StorageBackupFiles(this);
        Content = _sheet;
        _sheet.Title = Loc.T("backup.title");
        _sheet.Description = Loc.T("backup.detail");
        Build();
    }

    /// <summary>The key policy picked for the next export.</summary>
    public string Mode => _mode;

    /// <summary>The preview of the chosen file, or null.</summary>
    public BackupPreview? Preview => _preview;

    /// <summary>The entries that are ticked for import (indexes into the file).</summary>
    public IReadOnlyCollection<int> Ticked => _ticked;

    /// <summary>The message shown above the import list, or null.</summary>
    public string? ImportError => _importError;

    /// <summary>The message shown under the export button, or null.</summary>
    public string? ExportError => _exportError;

    /// <summary>The saved WebDAV settings as the server last reported them.</summary>
    internal WebDavStatus DavStatus => _davStatus;

    /// <summary>The message shown in the WebDAV section, or null.</summary>
    internal string? DavError => _davError;

    /// <summary>Opens the sheet with nothing chosen.</summary>
    public void Open()
    {
        _mode = ProviderBackupLogic.None;
        _exportPass = "";
        _exportError = null;
        _file = null;
        _importPass = "";
        _preview = null;
        _importError = null;
        _ticked.Clear();
        _davMode = ProviderBackupLogic.None;
        _davPassphrase = "";
        _davError = null;
        RenderExport();
        RenderImport();
        RenderDav();
        _sheet.IsOpen = true;
        _ = LoadDavAsync();
    }

    private void Build()
    {
        var exportHead = KitStyle.Text(Loc.T("backup.export.title"), ArcDesignTokenKind.TypeSM, ArcDesignTokenKind.Foreground, FontWeight.Medium);
        var mode = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        mode.Children.Add(PdUi.Text(Loc.T("backup.export.secrets"), 12.5, ArcDesignTokenKind.TextMuted));
        mode.Children.Add(PdUi.Choice(ProviderBackupLogic.Modes.Select(m => (m, ProviderBackupLogic.ModeLabel(m))), _mode, v => SetMode(v), 260, Loc.T("backup.export.secrets")));
        var exportSection = new StackPanel { Spacing = 12, Children = { exportHead, mode, _exportExtras } };

        var importHead = KitStyle.Text(Loc.T("backup.import.title"), ArcDesignTokenKind.TypeSM, ArcDesignTokenKind.Foreground, FontWeight.Medium);
        var choose = ProviderUi.Button(Loc.T("backup.import.choose"), LucideIconKind.Upload, onClick: () => _ = ChooseAsync());
        var importSection = new StackPanel { Spacing = 12, Children = { importHead, choose, _importArea } };
        var davSection = BuildDav();

        _sheet.Content = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = new StackPanel { Spacing = 24, Margin = new Thickness(0, 0, 14, 0), Children = { exportSection, importSection, davSection } },
        };
        RenderExport();
        RenderImport();
        RenderDav();
    }

    // ------------------------------------------------------------------ export

    /// <summary>Picks a key policy (the select calls this).</summary>
    internal void SetMode(string mode)
    {
        _mode = mode;
        _exportError = null;
        RenderExport();
    }

    /// <summary>Types the export passphrase (tests).</summary>
    internal void TypeExportPassphrase(string text)
    {
        _exportPass = text;
        RenderExport();
    }

    private ArcButton? _exportButton;

    private void RenderExport()
    {
        _exportExtras.Children.Clear();
        if (_mode != ProviderBackupLogic.None) _exportExtras.Children.Add(ProviderUi.Hint(Loc.T("backup.secrets.hint")));
        if (_mode == ProviderBackupLogic.Passphrase)
        {
            var box = PdUi.Secret(_exportPass, v => { _exportPass = v; UpdateExportButton(); }, 320, Loc.T("backup.passphrase"), Loc.T("backup.passphrase"));
            _exportExtras.Children.Add(box);
            _exportExtras.Children.Add(ProviderUi.Hint(Loc.T("backup.passphrase.hint")));
        }
        if (_mode == ProviderBackupLogic.Plaintext) _exportExtras.Children.Add(new Alert { Tone = AlertTone.Warning, Title = Loc.T("backup.plaintext.warn") });
        if (_exportError is not null) _exportExtras.Children.Add(new Alert { Tone = AlertTone.Danger, Title = Loc.T("common.error"), Content = _exportError });
        _exportButton = ProviderUi.Button(Loc.T("backup.export.run"), LucideIconKind.Download, ButtonVariant.Primary, onClick: () => _ = ExportAsync());
        _exportButton.HorizontalAlignment = HorizontalAlignment.Left;
        _exportExtras.Children.Add(_exportButton);
        UpdateExportButton();
    }

    private void UpdateExportButton()
    {
        if (_exportButton is not null) _exportButton.IsEnabled = !_busy && ProviderBackupLogic.CanExport(_mode, _exportPass);
    }

    /// <summary>Exports and saves the file (the export button); false when it failed or was cancelled.</summary>
    internal async Task<bool> ExportAsync()
    {
        _exportError = null;
        _busy = true;
        UpdateExportButton();
        try
        {
            var file = await _host.Queries.ExportBackupAsync(_mode, _mode == ProviderBackupLogic.Passphrase ? _exportPass : null);
            var text = file.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            if (!await _files.SaveAsync(ProviderBackupLogic.FileName(_host.Now), text)) return false;
            _host.Feedback.Success(Loc.T("backup.export.done", ("count", ProviderBackupLogic.Count(file))));
            _exportPass = "";
            return true;
        }
        catch (Exception e) when (e is ApiException or HttpRequestException or TaskCanceledException or IOException)
        {
            _exportError = e.Message;
            return false;
        }
        finally
        {
            _busy = false;
            RenderExport();
        }
    }

    // ------------------------------------------------------------------ import

    private async Task ChooseAsync()
    {
        string? text;
        try
        {
            text = await _files.ReadAsync();
        }
        catch (IOException e)
        {
            await LoadAsync(null, e.Message);
            return;
        }
        if (text is not null) await LoadAsync(text, null);
    }

    /// <summary>Takes the text of a chosen file: previews it straight away unless its keys need a passphrase first (tests call this directly).</summary>
    internal async Task LoadAsync(string? text, string? failure)
    {
        _preview = null;
        _file = null;
        _importError = failure;
        _ticked.Clear();
        if (failure is null)
        {
            _file = ProviderBackupLogic.ParseFile(text ?? "");
            if (_file is null) _importError = Loc.T("backup.import.badFile");
            else if (!ProviderBackupLogic.NeedsPassphrase(_file)) await PreviewAsync();
        }
        RenderImport();
    }

    /// <summary>A file from somewhere other than the file chooser (the WebDAV restore): previewed exactly like a chosen one (web <c>adopt</c>).</summary>
    internal async Task AdoptAsync(JsonObject file)
    {
        _preview = null;
        _importError = null;
        _ticked.Clear();
        _file = file;
        if (!ProviderBackupLogic.NeedsPassphrase(file)) await PreviewAsync();
        RenderImport();
    }

    /// <summary>Types the passphrase of an encrypted file (tests).</summary>
    internal void TypeImportPassphrase(string text) => _importPass = text;

    /// <summary>Previews the chosen file with the typed passphrase.</summary>
    internal async Task PreviewAsync()
    {
        if (_file is null) return;
        _importError = null;
        _busy = true;
        try
        {
            _preview = await _host.Queries.PreviewBackupAsync(_file, _importPass);
            _ticked.Clear();
            foreach (var i in ProviderBackupLogic.DefaultSelection(_preview)) _ticked.Add(i);
        }
        catch (Exception e) when (e is ApiException or HttpRequestException or TaskCanceledException)
        {
            _preview = null;
            _importError = e.Message;
        }
        finally
        {
            _busy = false;
            RenderImport();
        }
    }

    /// <summary>Imports the ticked entries and closes the sheet; failures stay on the sheet.</summary>
    internal async Task ImportAsync()
    {
        if (_file is null || _preview is null) return;
        var chosen = ProviderBackupLogic.Chosen(_preview, _ticked);
        if (chosen.Count == 0) return;
        _importError = null;
        _busy = true;
        RenderImport();
        try
        {
            var result = await _host.Queries.ImportBackupAsync(_file, _importPass, chosen);
            _host.Feedback.Success(ProviderBackupLogic.DoneText(result));
            _file = null;
            _preview = null;
            _importPass = "";
            _sheet.IsOpen = false;
        }
        catch (Exception e) when (e is ApiException or HttpRequestException or TaskCanceledException)
        {
            _importError = e.Message;
        }
        finally
        {
            _busy = false;
            RenderImport();
        }
    }

    private void RenderImport()
    {
        _importArea.Children.Clear();
        if (_file is not null && _preview is null && ProviderBackupLogic.NeedsPassphrase(_file))
        {
            _importArea.Children.Add(ProviderUi.Secondary(Loc.T("backup.import.encrypted")));
            var box = PdUi.Secret(_importPass, v => _importPass = v, 320, Loc.T("backup.passphrase"), Loc.T("backup.passphrase"));
            var preview = ProviderUi.Button(Loc.T("backup.import.preview"), onClick: () => _ = PreviewAsync());
            preview.IsEnabled = !_busy;
            _importArea.Children.Add(PdUi.Inline(8, box, preview));
        }
        if (_importError is not null) _importArea.Children.Add(new Alert { Tone = AlertTone.Danger, Title = Loc.T("common.error"), Content = _importError });
        if (_preview is null) return;

        if (_preview.Secrets == ProviderBackupLogic.None) _importArea.Children.Add(ProviderUi.Hint(Loc.T("backup.import.noKeys")));
        var rows = new StackPanel();
        for (var i = 0; i < _preview.Items.Count; i++)
        {
            if (i > 0) rows.Children.Add(PdUi.Divider());
            rows.Children.Add(Row(_preview.Items[i]));
        }
        _importArea.Children.Add(PdUi.Card(new ScrollViewer { MaxHeight = 320, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Content = rows }));
        var run = ProviderUi.Button(Loc.T("backup.import.run", ("count", ProviderBackupLogic.Chosen(_preview, _ticked).Count)), null, ButtonVariant.Primary, onClick: () => _ = ImportAsync());
        run.IsEnabled = !_busy && ProviderBackupLogic.Chosen(_preview, _ticked).Count > 0;
        run.HorizontalAlignment = HorizontalAlignment.Left;
        _importArea.Children.Add(run);
        _runImport = run;
    }

    private ArcButton? _runImport;

    // ------------------------------------------------------------------ WebDAV

    private Control BuildDav()
    {
        var head = KitStyle.Text(Loc.T("backup.webdav.title"), ArcDesignTokenKind.TypeSM, ArcDesignTokenKind.Foreground, FontWeight.Medium);
        _davUrlBox = PdUi.TextBox(_davUrl, v => { _davUrl = v; UpdateDavButtons(); }, null, true, Loc.T("backup.webdav.url"), "https://dav.example.com/remote.php/dav/files/me");
        _davUserBox = PdUi.TextBox(_davUser, v => _davUser = v, null, false, Loc.T("backup.webdav.username"), Loc.T("backup.webdav.username"));
        _davPassBox = PdUi.Secret(_davPass, v => _davPass = v, 0, Loc.T("backup.webdav.password"), Loc.T("backup.webdav.password"));
        _davPassBox.Width = double.NaN;
        _davFolderBox = PdUi.TextBox(_davFolder, v => _davFolder = v, null, true, Loc.T("backup.webdav.folder"), Loc.T("backup.webdav.folder"));
        var login = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 8 };
        login.Children.Add(_davUserBox);
        Grid.SetColumn(_davPassBox, 1);
        login.Children.Add(_davPassBox);

        _davSave = ProviderUi.Button(Loc.T("backup.webdav.save"), null, ButtonVariant.Primary, onClick: () => _ = SaveDavAsync());
        _davTest = ProviderUi.Button(Loc.T("backup.webdav.test"), onClick: () => _ = TestDavAsync());
        _davForget = ProviderUi.Button(Loc.T("backup.webdav.forget"), onClick: () => _ = ForgetDavAsync());
        var buttons = PdUi.Inline(8, _davSave, _davTest, _davForget);

        UpdateDavButtons();
        return new StackPanel
        {
            Spacing = 12,
            Children = { head, ProviderUi.Hint(Loc.T("backup.webdav.hint")), _davUrlBox, login, _davFolderBox, buttons, _davArea },
        };
    }

    private void UpdateDavButtons()
    {
        if (_davSave is not null) _davSave.IsEnabled = !_davBusy && _davUrl.Trim().Length > 0;
        if (_davTest is not null) _davTest.IsEnabled = !_davBusy && _davStatus.Configured;
        if (_davForget is not null)
        {
            _davForget.IsVisible = _davStatus.Configured;
            _davForget.IsEnabled = !_davBusy;
        }
        if (_davUpload is not null) _davUpload.IsEnabled = !_davBusy && ProviderBackupLogic.CanExport(_davMode, _davPassphrase);
    }

    // The part that exists once the settings are saved: the key policy, "back up now" and "restore from the cloud", then the error.
    private void RenderDav()
    {
        _davArea.Children.Clear();
        _davUpload = null;
        if (_davStatus.Configured)
        {
            var mode = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            mode.Children.Add(PdUi.Text(Loc.T("backup.export.secrets"), 12.5, ArcDesignTokenKind.TextMuted));
            mode.Children.Add(PdUi.Choice(ProviderBackupLogic.WebDavModes.Select(m => (m, ProviderBackupLogic.ModeLabel(m))), _davMode, v => SetDavMode(v), 260, Loc.T("backup.export.secrets")));
            _davArea.Children.Add(mode);
            if (_davMode == ProviderBackupLogic.Passphrase)
            {
                var name = $"{Loc.T("backup.webdav.title")} {Loc.T("backup.passphrase")}";
                _davArea.Children.Add(PdUi.Secret(_davPassphrase, v => { _davPassphrase = v; UpdateDavButtons(); }, 320, name, Loc.T("backup.passphrase")));
            }
            _davUpload = ProviderUi.Button(Loc.T("backup.webdav.upload"), LucideIconKind.CloudUpload, ButtonVariant.Primary, onClick: () => _ = UploadDavAsync());
            var restore = ProviderUi.Button(Loc.T("backup.webdav.restore"), LucideIconKind.CloudDownload, onClick: () => _ = RestoreDavAsync());
            restore.IsEnabled = !_davBusy;
            _davArea.Children.Add(PdUi.Inline(8, _davUpload, restore));
        }
        if (_davError is not null) _davArea.Children.Add(new Alert { Tone = AlertTone.Danger, Title = Loc.T("common.error"), Content = _davError });
        UpdateDavButtons();
    }

    /// <summary>Picks the key policy of the next cloud backup (the select calls this).</summary>
    internal void SetDavMode(string mode)
    {
        _davMode = mode;
        _davError = null;
        RenderDav();
    }

    /// <summary>Types the cloud backup's passphrase (tests).</summary>
    internal void TypeDavPassphrase(string text)
    {
        _davPassphrase = text;
        RenderDav();
    }

    /// <summary>Fills the settings form as if typed (tests).</summary>
    internal void TypeDavSettings(string url, string username, string password, string folder)
    {
        (_davUrl, _davUser, _davPass, _davFolder) = (url, username, password, folder);
        if (_davUrlBox is not null) _davUrlBox.Text = url;
        if (_davUserBox is not null) _davUserBox.Text = username;
        if (_davPassBox is not null) _davPassBox.Text = password;
        if (_davFolderBox is not null) _davFolderBox.Text = folder;
        UpdateDavButtons();
    }

    // Takes what the server reports as the form's content; the password box is emptied (the server keeps the stored one).
    private void ApplyDavStatus(WebDavStatus status)
    {
        _davStatus = status;
        _davPass = "";
        TypeDavSettings(status.Url, status.Username, "", status.Folder);
        if (_davPassBox is not null) _davPassBox.Placeholder = Loc.T(status.HasPassword ? "backup.webdav.passwordKept" : "backup.webdav.password");
    }

    /// <summary>Reads the saved WebDAV settings (when the sheet opens). A failure is shown, not thrown.</summary>
    internal async Task LoadDavAsync()
    {
        try
        {
            ApplyDavStatus(await _host.Queries.WebDavStatusAsync());
        }
        catch (Exception e) when (e is ApiException or HttpRequestException or TaskCanceledException)
        {
            _davError = e.Message;
        }
        RenderDav();
    }

    private async Task<bool> RunDavAsync(Func<Task> work)
    {
        _davError = null;
        _davBusy = true;
        UpdateDavButtons();
        try
        {
            await work();
            return true;
        }
        catch (Exception e) when (e is ApiException or HttpRequestException or TaskCanceledException)
        {
            _davError = e.Message;
            return false;
        }
        finally
        {
            _davBusy = false;
            RenderDav();
        }
    }

    /// <summary>Saves the address, login and folder (the save button).</summary>
    internal Task<bool> SaveDavAsync() => RunDavAsync(async () =>
    {
        ApplyDavStatus(await _host.Queries.SaveWebDavAsync(_davUrl, _davUser, _davFolder, _davPass));
        _host.Feedback.Success(Loc.T("backup.webdav.saved"));
    });

    /// <summary>Checks the connection (the test button).</summary>
    internal Task<bool> TestDavAsync() => RunDavAsync(async () =>
    {
        await _host.Queries.TestWebDavAsync();
        _host.Feedback.Success(Loc.T("backup.webdav.testOk"));
    });

    /// <summary>Drops the saved settings (the remove button).</summary>
    internal Task<bool> ForgetDavAsync() => RunDavAsync(async () => ApplyDavStatus(await _host.Queries.ForgetWebDavAsync()));

    /// <summary>Backs the providers up to the cloud (the back-up button).</summary>
    internal Task<bool> UploadDavAsync() => RunDavAsync(async () =>
    {
        var count = await _host.Queries.UploadWebDavAsync(_davMode, _davPassphrase);
        _host.Feedback.Success(Loc.T("backup.webdav.uploadDone", ("count", count)));
        _davPassphrase = "";
    });

    /// <summary>Fetches the latest cloud backup and previews it like a chosen file; nothing is imported until the person confirms.</summary>
    internal async Task<bool> RestoreDavAsync()
    {
        JsonObject? file = null;
        var ok = await RunDavAsync(async () => file = await _host.Queries.RestoreWebDavAsync());
        if (ok && file is not null) await AdoptAsync(file);
        return ok;
    }

    private Control Row(BackupItem item)
    {
        var enabled = ProviderBackupLogic.Selectable(item);
        var box = new Checkbox { IsChecked = _ticked.Contains(item.Index) && enabled, IsEnabled = enabled, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) };
        Avalonia.Automation.AutomationProperties.SetName(box, item.Name);
        box.IsCheckedChanged += (_, _) =>
        {
            if (box.IsChecked == true) _ticked.Add(item.Index); else _ticked.Remove(item.Index);
            if (_preview is null || _runImport is null) return;
            var n = ProviderBackupLogic.Chosen(_preview, _ticked).Count;
            _runImport.Content = Loc.T("backup.import.run", ("count", n));
            _runImport.IsEnabled = !_busy && n > 0;
        };

        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var name = KitStyle.Text(item.Name, ArcDesignTokenKind.TypeSM, ArcDesignTokenKind.Foreground, FontWeight.Medium);
        ToolTip.SetTip(name, item.Name);
        title.Children.Add(name);
        title.Children.Add(KitBadge.Create(Loc.T("backup.status." + item.Status), ToTone(ProviderBackupLogic.ToneOf(item.Status))));

        var info = new StackPanel { Spacing = 2 };
        info.Children.Add(title);
        if (item.Addresses.Count > 0)
        {
            var addresses = KitStyle.Text(string.Join(" / ", item.Addresses), ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextMuted);
            addresses.Token(TextBlock.FontFamilyProperty, ArcDesignTokenKind.MonoFontFamily);
            info.Children.Add(addresses);
        }
        var meta = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        if (item.HasKey)
        {
            var key = KitStyle.Text(item.KeyMasked ?? "••••", ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextMuted);
            key.Token(TextBlock.FontFamilyProperty, ArcDesignTokenKind.MonoFontFamily);
            meta.Children.Add(key);
        }
        meta.Children.Add(KitStyle.Text(Loc.T("backup.models", ("count", item.ModelCount)), ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextMuted));
        info.Children.Add(meta);
        if (item.ExistingName is not null && item.Status != "new") info.Children.Add(ProviderUi.Hint(item.ExistingName));
        if (item.Error is not null) info.Children.Add(ProviderUi.Hint(item.Error).Token(TextBlock.ForegroundProperty, ArcDesignTokenKind.Danger));
        if (item.Ignored.Count > 0) info.Children.Add(ProviderUi.Hint(Loc.T("import.ignored", ("fields", string.Join(", ", item.Ignored)))).Token(TextBlock.ForegroundProperty, ArcDesignTokenKind.Warning));

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 12, Margin = new Thickness(16, 12), Opacity = enabled ? 1 : 0.6 };
        row.Children.Add(box);
        Grid.SetColumn(info, 1);
        row.Children.Add(info);
        return row;
    }

    private static Tone ToTone(ImportTone t) => t switch { ImportTone.Green => Tone.Green, ImportTone.Orange => Tone.Orange, ImportTone.Red => Tone.Red, _ => Tone.Neutral };
}
