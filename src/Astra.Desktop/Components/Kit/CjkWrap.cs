using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using TKCode.Arc;

namespace Astra.Desktop.Components.Kit;

/// <summary>
/// CJK text in Arc's <see cref="EmptyState"/> wraps the way the web's does. Arc balances the title and description lines
/// (<c>text-wrap: balance</c>) by narrowing the text until one more line would appear; for Chinese, where any character is a break
/// opportunity, that splits a sentence into two half-width lines broken in the middle of a word ("经过 Astra 的每次请求都会 / 记录…").
/// The web's browser fills the 288 px line instead (requests__empty: "…都会记录在这 / 里，包括…"). So for copy that contains CJK the
/// balancing is turned off and the text simply wraps at the column width; Latin copy keeps Arc's balanced lines.
/// Installed once for every <see cref="EmptyState"/> in the app (<see cref="Theme.KitStyles.Install"/>), the kit's own
/// <see cref="EmptyStateView"/> included.
/// </summary>
public static class CjkWrap
{
    private static bool _installed;

    /// <summary>Registers the class handlers (idempotent).</summary>
    public static void Install()
    {
        if (_installed) return;
        _installed = true;
        TemplatedControl.TemplateAppliedEvent.AddClassHandler<EmptyState>((state, _) => Apply(state));
        EmptyState.TitleProperty.Changed.AddClassHandler<EmptyState>((state, _) => Apply(state));
        EmptyState.DescriptionProperty.Changed.AddClassHandler<EmptyState>((state, _) => Apply(state));
    }

    /// <summary>True when <paramref name="text"/> has a Han, kana or hangul character (where the browser breaks between any two).</summary>
    public static bool HasCjk(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        foreach (var c in text)
        {
            if (c is >= '぀' and <= 'ヿ' // kana
                or >= '㐀' and <= '䶿' // CJK extension A
                or >= '一' and <= '鿿' // CJK unified
                or >= '가' and <= '힯' // hangul
                or >= '豈' and <= '﫿' // compatibility ideographs
                or >= '　' and <= '〿' // CJK punctuation
                or >= '＀' and <= '￯') // full-width forms
                return true;
        }
        return false;
    }

    private static void Apply(EmptyState state)
    {
        SetBalanced(state, "PART_Title", !HasCjk(state.Title));
        SetBalanced(state, "PART_Description", !HasCjk(state.Description));
    }

    private static void SetBalanced(EmptyState state, string part, bool balanced)
    {
        // The text part is Arc-internal (MessagesTextSwap); its IsBalanced property is found through the property registry, not reflection.
        if (state.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == part && c.TemplatedParent == state) is not { } text) return;
        if (AvaloniaPropertyRegistry.Instance.FindRegistered(text, "IsBalanced") is StyledProperty<bool> property) text.SetValue(property, balanced);
    }
}
