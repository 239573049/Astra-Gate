using TKCode.Arc;

namespace Astra.Desktop.Components.Kit;

/// <summary>The web's status tones (<c>Tone</c> in controls.tsx): badges and dots.</summary>
public enum Tone
{
    /// <summary>Muted, no meaning.</summary>
    Neutral,

    /// <summary>The accent color (web "accent": a badge in <c>info</c>).</summary>
    Accent,

    /// <summary>Success / healthy.</summary>
    Green,

    /// <summary>Warning / degraded.</summary>
    Orange,

    /// <summary>Danger / failed.</summary>
    Red,
}

/// <summary>Arc badges the way web <c>Badge</c> builds them (<c>tone</c> mapped, <c>size="sm"</c>).</summary>
public static class KitBadge
{
    /// <summary>Web tone -> Arc badge tone (neutral, accent->info, green->success, orange->warning, red->danger).</summary>
    public static BadgeTone ToArc(Tone tone) => tone switch
    {
        Tone.Accent => BadgeTone.Info,
        Tone.Green => BadgeTone.Success,
        Tone.Orange => BadgeTone.Warning,
        Tone.Red => BadgeTone.Danger,
        _ => BadgeTone.Neutral,
    };

    /// <summary>A small badge with <paramref name="text"/>.</summary>
    public static Badge Create(string text, Tone tone = Tone.Neutral, string? toolTip = null)
    {
        var badge = new Badge { Tone = ToArc(tone), Size = BadgeSize.Small, Content = text, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left };
        if (toolTip is not null) Avalonia.Controls.ToolTip.SetTip(badge, toolTip);
        return badge;
    }
}
