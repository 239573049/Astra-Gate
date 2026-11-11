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
        RenderExport();
        RenderImport();
        _sheet.IsOpen = true;
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

        _sheet.Content = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = new StackPanel { Spacing = 24, Margin = new Thickness(0, 0, 14, 0), Children = { exportSection, importSection } },
        };
        RenderExport();
        RenderImport();
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
