namespace Astra.Desktop.Core.Connect;

/// <summary>
/// Where a join link the operating system handed to the app waits for the Clients page: the link can arrive before any page exists (the app was
/// started by it) or while another page is open. The app posts it, shows the Clients page, and the page takes it and opens the connect sheet.
/// </summary>
public static class JoinLinkInbox
{
    private static readonly object Gate = new();
    private static JoinLink? _pending;

    /// <summary>Raised (on the posting thread) after a link was posted; a page that is already open takes it then.</summary>
    public static event Action? Arrived;

    /// <summary>Leaves a link for the Clients page.</summary>
    public static void Post(JoinLink link)
    {
        lock (Gate) _pending = link;
        Arrived?.Invoke();
    }

    /// <summary>The waiting link, once: null when there is none or it was already taken.</summary>
    public static JoinLink? Take()
    {
        lock (Gate)
        {
            var link = _pending;
            _pending = null;
            return link;
        }
    }
}
