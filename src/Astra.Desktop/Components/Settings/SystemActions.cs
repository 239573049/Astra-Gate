using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;

namespace Astra.Desktop.Components.Settings;

/// <summary>Clipboard and browser hand-offs of the pages (web <c>systemActions.copy</c> and <c>window.open(url, '_blank')</c>).</summary>
public static class SystemActions
{
    /// <summary>Copies text; false when there is no clipboard (headless runs) or the system refused.</summary>
    public static async Task<bool> CopyAsync(Visual? owner, string text)
    {
        if (owner is null || TopLevel.GetTopLevel(owner)?.Clipboard is not { } clipboard) return false;
        try
        {
            await clipboard.SetTextAsync(text);
            return true;
        }
        catch (Exception e) when (e is InvalidOperationException or System.Runtime.InteropServices.COMException or IOException)
        {
            return false;
        }
    }

    /// <summary>Opens an http(s) address in the default browser; anything else is refused.</summary>
    public static async Task<bool> OpenUrlAsync(Visual? owner, string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return false;
        if (owner is null || TopLevel.GetTopLevel(owner)?.Launcher is not { } launcher) return false;
        try
        {
            return await launcher.LaunchUriAsync(uri);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
