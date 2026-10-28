using Astra.Desktop.Components.Kit;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using TKCode.Arc.DesignTokens;

namespace Astra.Desktop.Components.Tokens;

/// <summary>
/// Web <c>Field</c> for editors that have no label of their own (a pool editor, a list of budgets, a segmented control): a label, the
/// editor, a hint under it that wraps over as many lines as it needs, and an error that replaces the hint. (ARC-GAP: Arc's
/// <c>Field.Description</c> clips a long hint to one line next to wide content, so the kit's <c>FormField.Wrap</c> is not used here.)
/// </summary>
public sealed class FieldBlock : UserControl
{
    private readonly TextBlock _hint;
    private readonly TextBlock _error;

    /// <summary>A field of <paramref name="editor"/> with <paramref name="label"/> and an optional <paramref name="hint"/>.</summary>
    public FieldBlock(string label, Control editor, string? hint = null)
    {
        var title = KitStyle.Text(label, ArcDesignTokenKind.TypeSM, ArcDesignTokenKind.Foreground, FontWeight.Medium);
        title.Margin = new Thickness(0, 0, 0, 6);
        _hint = KitStyle.Text(hint, ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextMuted);
        _hint.FontSize = KitStyle.Type12;
        _hint.TextWrapping = TextWrapping.Wrap;
        _hint.TextTrimming = TextTrimming.None;
        _hint.Margin = new Thickness(0, 6, 0, 0);
        _hint.IsVisible = !string.IsNullOrEmpty(hint);
        _error = KitStyle.Text(null, ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.Danger);
        _error.FontSize = KitStyle.Type12;
        _error.TextWrapping = TextWrapping.Wrap;
        _error.TextTrimming = TextTrimming.None;
        _error.Margin = new Thickness(0, 6, 0, 0);
        _error.IsVisible = false;
        Content = new StackPanel { Children = { title, editor, _hint, _error } };
    }

    /// <summary>An error message; shown in place of the hint while set.</summary>
    public string? Error
    {
        get => _error.Text;
        set
        {
            _error.Text = value;
            _error.IsVisible = !string.IsNullOrEmpty(value);
            _hint.IsVisible = !_error.IsVisible && !string.IsNullOrEmpty(_hint.Text);
        }
    }
}
