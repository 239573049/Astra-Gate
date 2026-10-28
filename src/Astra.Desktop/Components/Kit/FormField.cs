using Avalonia.Controls;
using TKCode.Arc;

namespace Astra.Desktop.Components.Kit;

/// <summary>
/// Web <c>Field</c> (label + hint + error around a control). Arc's text inputs, selects and pickers already carry
/// <c>Label</c> / <c>Description</c> / <c>ErrorMessage</c>, so those need no wrapper; this builds Arc's own <see cref="Field"/>
/// for editors without them (a token pool editor, a group of switches, a custom row) so every form row has the same structure.
/// </summary>
public static class FormField
{
    /// <summary>Wraps <paramref name="editor"/> in an Arc field with the given label, hint (Arc: description) and error.</summary>
    public static Field Wrap(string label, Control editor, string? hint = null, string? error = null) => new()
    {
        Label = label,
        Description = hint,
        ErrorMessage = error,
        Content = editor,
    };
}
