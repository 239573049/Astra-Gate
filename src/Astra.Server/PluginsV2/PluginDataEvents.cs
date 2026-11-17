using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Nodes;
using Astra.Data;
using Astra.Plugins.V2;

namespace Astra.Server.PluginsV2;

/// <summary>
/// The v2 host events about providers and accounts (design/plugin-v2 09 §4.1): <c>provider.changed</c> and
/// <c>account.changed</c>, from the repositories' <c>Changed</c> signals. Changes to one row within <see cref="Window"/>
/// become one event (deleted beats created beats updated), whose payload is read from the row when it is sent — the event
/// says "re-read", it is not a log of every write. Nothing is read or queued while no plugin listens. Payloads carry no
/// secret, token or e-mail address.
/// </summary>
public sealed class PluginDataEvents
{
    public const string ProviderChanged = "provider.changed";
    public const string AccountChanged = "account.changed";

    /// <summary>How long changes to one row are gathered before the event goes out.</summary>
    public static TimeSpan Window { get; set; } = TimeSpan.FromMilliseconds(500);

    private readonly AstraDatabase _db;
    private readonly PluginV2Host _host;
    private readonly ConcurrentDictionary<(string Event, string Id), string> _pending = new();

    public PluginDataEvents(AstraDatabase db, PluginV2Host host)
    {
        _db = db;
        _host = host;
        db.Providers.Changed += (id, change) => Note(ProviderChanged, id, change);
        db.Accounts.Changed += (id, change) => Note(AccountChanged, id, change);
    }

    private void Note(string name, string id, string change)
    {
        if (!_host.HasSubscribers(name)) return;
        var first = true;
        _pending.AddOrUpdate((name, id), change, (_, before) =>
        {
            first = false;
            return Stronger(before, change);
        });
        if (first) _ = FlushLaterAsync(name, id);
    }

    private static string Stronger(string a, string b) => Rank(a) >= Rank(b) ? a : b;

    private static int Rank(string change) => change switch { RowChanges.Deleted => 2, RowChanges.Created => 1, _ => 0 };

    private async Task FlushLaterAsync(string name, string id)
    {
        try
        {
            await Task.Delay(Window);
            if (!_pending.TryRemove((name, id), out var change)) return;
            var payload = name == ProviderChanged ? await ProviderPayloadAsync(id, change) : await AccountPayloadAsync(id, change);
            _host.Publish(name, payload);
        }
        catch (Exception)
        {
            // best effort, like every host event (02 §7)
        }
    }

    private async Task<JsonObject> ProviderPayloadAsync(string id, string change)
    {
        var o = new JsonObject { ["providerId"] = id, ["change"] = change };
        if (change != RowChanges.Deleted && await _db.Providers.GetAsync(id) is { } p)
        {
            o["name"] = p.Name;
            o["enabled"] = p.Enabled;
        }
        return o;
    }

    private async Task<JsonObject> AccountPayloadAsync(string id, string change)
    {
        var o = new JsonObject { ["accountId"] = id, ["change"] = change };
        if (change != RowChanges.Deleted && await _db.Accounts.GetAsync(id) is { } a)
        {
            o["providerId"] = a.ProviderId;
            o["status"] = a.Status;
            o["enabled"] = a.Enabled;
            o["cooldownUntil"] = a.CooldownUntilUtc?.ToString("O", CultureInfo.InvariantCulture);
        }
        return o;
    }
}
