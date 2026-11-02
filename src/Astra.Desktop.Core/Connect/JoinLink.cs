namespace Astra.Desktop.Core.Connect;

/// <summary>
/// An employee join link, <c>astra://join?gateway=https://astra.example.com&amp;token=ij_…</c>, as the console's "generate join link" makes
/// it: the unified gateway's address and a one-time code. It holds no key; the local server exchanges the code for it
/// (<c>POST /api/connect/join</c>). Opening a link only asks the person to confirm: nothing is exchanged or written before that.
/// </summary>
public sealed record JoinLink(string GatewayUrl, string Token)
{
    /// <summary>The URL scheme the desktop app registers with the operating system.</summary>
    public const string Scheme = "astra";

    private const int MaxLength = 2048;

    /// <summary>The link a command-line argument or a pasted text holds, or null when it is not a well-formed join link.</summary>
    public static JoinLink? TryParse(string? text)
    {
        var raw = text?.Trim();
        if (string.IsNullOrEmpty(raw) || raw.Length > MaxLength) return null;
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || !uri.Scheme.Equals(Scheme, StringComparison.OrdinalIgnoreCase)) return null;
        if (!uri.Host.Equals("join", StringComparison.OrdinalIgnoreCase)) return null;
        string? gateway = null, token = null;
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var at = pair.IndexOf('=');
            if (at <= 0) continue;
            var value = Uri.UnescapeDataString(pair[(at + 1)..].Replace('+', ' '));
            switch (pair[..at])
            {
                case "gateway": gateway = value; break;
                case "token": token = value; break;
            }
        }
        if (string.IsNullOrWhiteSpace(token) || token.Length > 200 || token.Any(char.IsWhiteSpace)) return null;
        if (!Uri.TryCreate(gateway, UriKind.Absolute, out var gw) || gw.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(gw.UserInfo)) return null;
        return new JoinLink(gw.GetLeftPart(UriPartial.Path).TrimEnd('/'), token);
    }

    /// <summary>The first command-line argument that is a join link.</summary>
    public static JoinLink? FromArgs(IEnumerable<string> args) => args.Select(TryParse).FirstOrDefault(l => l is not null);

    /// <summary>The address a person recognises: scheme and host (and port), without any path.</summary>
    public string Display => Uri.TryCreate(GatewayUrl, UriKind.Absolute, out var uri) ? uri.GetLeftPart(UriPartial.Authority) : GatewayUrl;
}
