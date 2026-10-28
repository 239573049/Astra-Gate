using Astra.Desktop.Components.Kit;
using Astra.Desktop.Core.Clients;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Providers;

namespace Astra.Desktop.Components.Clients;

/// <summary>
/// The protocol of each model a client lists (web <c>ClientProtocolsGroup</c>): "automatic" — its native protocol when the client speaks
/// it, else the client's default, translated by the gateway — or the user's choice (<c>extras.protocolOverrides</c>). Shown for clients
/// that can use more than one protocol (<c>ClientInfoDto.Protocols</c>, which drives <c>ClientProtocols.For</c> on the server).
/// </summary>
public static class ClientProtocolsGroup
{
    /// <summary>The group for <paramref name="models"/>; <paramref name="supported"/> lists the client's protocols, its default first.</summary>
    public static SettingsGroup Create(IReadOnlyList<ProtocolModel> models, IReadOnlyList<string> supported, IReadOnlyDictionary<string, string> values,
        Action<Dictionary<string, string>> onChange, bool enabled)
    {
        var current = values;
        var group = new SettingsGroup
        {
            Title = Loc.T("clients.protocols.title"),
            Footer = enabled ? Loc.T("clients.protocols.footerEnabled") : Loc.T("clients.protocols.footer"),
        };
        foreach (var m in models)
        {
            var (auto, translated) = ClientExtras.AutoProtocol(m, supported);
            var native = m.Native.Where(supported.Contains).ToList();
            var detail = native.Count > 0
                ? Loc.T("clients.protocols.native", ("protocols", string.Join(" · ", native.Select(ProviderProtocols.Name))))
                : Loc.T("clients.protocols.noNative");
            var options = new List<(string, string, bool)>
            {
                (ClientExtras.AutoValue, Loc.T(translated ? "clients.protocols.autoTranslated" : "clients.protocols.auto", ("protocol", ProviderProtocols.Name(auto))), false),
            };
            options.AddRange(supported.Select(p => (p, ProviderProtocols.Name(p), false)));
            var selected = values.TryGetValue(m.Id, out var v) && supported.Contains(v) ? v : ClientExtras.AutoValue;
            var id = m.Id;
            var select = ClientUi.Select(options, selected, value =>
            {
                var next = ClientExtras.SetOverride(current, id, value);
                current = next;
                onChange(next);
            }, m.Name, width: 240);
            var row = new SettingsRow { Label = m.Name, Detail = detail };
            row.Trailing.Add(select);
            group.Rows.Add(row);
        }
        return group;
    }
}
