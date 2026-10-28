using Astra.Api.Contracts;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Core.Accounts;
using Astra.Desktop.Core.I18n;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using ArcButton = TKCode.Arc.Button;

namespace Astra.Desktop.Components.Accounts;

/// <summary>
/// The unified account import sheet (web AccountImportSheet.tsx): paste — one or many — refresh tokens, setup-tokens, credential JSON,
/// API keys or GitHub tokens. "Preview" only classifies and de-duplicates on the server (nothing goes upstream, nothing is written);
/// "import" exchanges / checks each entry and stores it; the result lists every entry, masked. The entry count under the box follows
/// the server's splitting rules (<see cref="AccountImport.CountEntries"/>). The web sheet has no file drop zone, so neither has this.
/// </summary>
internal sealed class AccountImportSheet : Sheet
{
    private readonly SubscriptionAccountsModel _model;
    private readonly Textarea _text = new() { IsLabelVisible = false, };
    private readonly Input _proxy = new();
    private readonly TextBlock _formats = AccountsUi.Text("", 11.5, ArcDesignTokenKind.TextSecondary, wrap: true);
    private readonly TextBlock _count = AccountsUi.Text("", 11, ArcDesignTokenKind.TextMuted);
    private readonly Alert _rotate = new() { Tone = AlertTone.Info };
    private readonly ArcButton _preview;
    private readonly ArcButton _run;
    private readonly StackPanel _result = new() { Spacing = 4 };
    private ImportMode _mode = ImportMode.Batch;
    private AccountImportInfoDto _info = new(false, false, [], 0);
    private bool? _pendingDry;

    /// <summary>Creates the (closed) sheet.</summary>
    public AccountImportSheet(SubscriptionAccountsModel model)
    {
        _model = model;
        SheetWidth = 560;
        _text.Token(Avalonia.Controls.Primitives.TemplatedControl.FontFamilyProperty, ArcDesignTokenKind.MonoFontFamily);
        _proxy.Token(Avalonia.Controls.Primitives.TemplatedControl.FontFamilyProperty, ArcDesignTokenKind.MonoFontFamily);
        _text.TextChanged += (_, _) => Buttons();
        _preview = AccountsUi.Button("", () => _ = SubmitAsync(true), size: ControlSize.Medium);
        _run = AccountsUi.Button("", () => _ = SubmitAsync(false), ButtonVariant.Primary, size: ControlSize.Medium);

        var bar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        bar.Children.Add(_count);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _preview, _run } };
        Grid.SetColumn(buttons, 1);
        bar.Children.Add(buttons);
        Content = new StackPanel { Spacing = 12, Children = { _formats, _text, _proxy, _rotate, bar, _result } };
    }

    /// <summary>The text in the box (scenes fill it).</summary>
    public string Text { get => _text.Text ?? ""; set => _text.Text = value; }

    /// <summary>Opens the sheet for <paramref name="mode"/> with an empty box and no result.</summary>
    public void Open(ImportMode mode, AccountImportInfoDto info)
    {
        _mode = mode;
        _info = info;
        var label = AccountImport.Label(mode);
        Title = Loc.T(AccountImport.TitleKey(mode));
        Description = Loc.T("providers.accounts.import.desc");
        var hint = AccountImport.FormatsHint(info, mode);
        _formats.Text = hint;
        _formats.IsVisible = hint is not null;
        _text.Text = "";
        _text.Placeholder = label;
        _text.Label = label;
        // web rows={8} / rows={3}: Arc's textarea is at least ~110 px tall (three rows fit), so only the batch box gets a frame height.
        _text.FrameHeight = mode == ImportMode.Batch ? 16 + 8 * 20 : double.NaN;
        _proxy.Label = Loc.T("providers.accounts.import.proxy");
        _proxy.Placeholder = string.IsNullOrEmpty(_model.Provider.HttpProxy) ? Loc.T("providers.accounts.proxyFallbackSystem") : _model.Provider.HttpProxy;
        _rotate.Title = Loc.T("providers.accounts.import.rotateWarning");
        _rotate.IsVisible = AccountImport.TakesRefreshTokens(info, mode);
        _preview.Content = Loc.T("providers.accounts.import.preview");
        _preview.IsVisible = mode == ImportMode.Batch;
        _run.Content = Loc.T("providers.accounts.import.run");
        _result.Children.Clear();
        _pendingDry = null;
        Buttons();
        if (!IsOpen) IsOpen = true;
    }

    private void Buttons()
    {
        var n = AccountImport.CountEntries(_text.Text);
        _count.Text = Loc.T("providers.accounts.import.count", ("n", n));
        _preview.IsEnabled = n > 0 && _pendingDry is null;
        _preview.IsLoading = _pendingDry == true;
        _run.IsEnabled = AccountImport.CanRun(n, _info) && _pendingDry is null;
        _run.IsLoading = _pendingDry == false;
    }

    /// <summary>Runs a preview (<paramref name="dryRun"/>) or the import and shows the result.</summary>
    public async Task SubmitAsync(bool dryRun)
    {
        var text = _text.Text ?? "";
        _pendingDry = dryRun;
        Buttons();
        try
        {
            var r = await _model.Api.ImportAsync(_model.Provider.Id, AccountImport.Request(text, _mode, dryRun, _proxy.Text));
            ShowResult(r, dryRun);
            if (dryRun) return;
            var summary = AccountImport.Summary(r, false);
            if (r.Failed > 0)
            {
                _model.Feedback.Info(summary);
                return;
            }
            _model.Feedback.Success(summary);
            // All in: a single-entry sheet closes; the batch sheet clears the box and keeps the result for reading.
            if (_mode != ImportMode.Batch) IsOpen = false;
            else _text.Text = "";
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _model.Feedback.Error(AccountRules.ErrorText(e));
        }
        finally
        {
            _pendingDry = null;
            Buttons();
        }
    }

    private void ShowResult(BatchImportResultDto r, bool dryRun)
    {
        _result.Children.Clear();
        _result.Children.Add(AccountsUi.Text(AccountImport.Summary(r, dryRun), 12, weight: FontWeight.Medium, wrap: true));
        var rows = r.Items.Select(Row).ToList();
        var list = new ScrollViewer { MaxHeight = 288, Content = AccountsUi.Divided(rows, ArcDesignTokenKind.Border), HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        _result.Children.Add(AccountsUi.Card(list));
    }

    private static Control Row(BatchImportItemDto item)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("24,112,Auto,Auto,*"), ColumnSpacing = 8, Margin = new Thickness(12, 6) };
        var index = AccountsUi.Text(item.Index.ToString(System.Globalization.CultureInfo.InvariantCulture), 12, ArcDesignTokenKind.TextMuted);
        index.VerticalAlignment = VerticalAlignment.Top;
        grid.Children.Add(index);
        var source = AccountsUi.Selectable(item.Source, 12, mono: true);
        source.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(source, 1);
        grid.Children.Add(source);
        var badge = AccountsUi.Badge(Loc.T(AccountImport.StatusKey(item.Status)), AccountImport.StatusTone(item.Status));
        badge.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(badge, 2);
        grid.Children.Add(badge);
        if (AccountImport.KindText(item.Kind) is { } kind)
        {
            var k = AccountsUi.Text(kind, 11, ArcDesignTokenKind.TextMuted);
            k.VerticalAlignment = VerticalAlignment.Top;
            k.Margin = new Thickness(0, 1, 0, 0);
            Grid.SetColumn(k, 3);
            grid.Children.Add(k);
        }
        var text = AccountsUi.Selectable(AccountImport.RowText(item), 12, color: ArcDesignTokenKind.TextSecondary);
        text.TextWrapping = TextWrapping.Wrap;
        text.TextTrimming = TextTrimming.None;
        text.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(text, 4);
        grid.Children.Add(text);
        return grid;
    }
}
