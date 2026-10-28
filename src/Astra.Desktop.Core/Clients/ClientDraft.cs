using System.Text.Json.Nodes;
using Astra.Api.Contracts;
using Astra.Core.Clients;

namespace Astra.Desktop.Core.Clients;

/// <summary>
/// The selected client's unsaved choices (web <c>ClientPanel</c>'s <c>useState</c>s): default model, pinned account, token, Claude Desktop
/// roles, Claude Code model tiers, client options and per-model protocol overrides — and the rules that tie them to the saved client:
/// what counts as changed, the <c>extras</c> an enable / apply sends, and when a server update resets a field (the web's
/// <c>useEffect</c>s, keyed on the saved value so an unrelated refetch never throws a pending edit away).
/// </summary>
public sealed class ClientDraft
{
    private string _kind = "";
    private string? _savedModel;
    private string? _savedAccountKey;
    private string _savedToken = "default";
    private string _savedRolesKey = "";
    private string _savedSlotsKey = "";
    private string _savedOptionsKey = "";
    private string _savedOverridesKey = "";
    private Dictionary<string, string> _suggested = new(StringComparer.Ordinal);
    private string _suggestedFor = "\0";

    /// <summary>The client the draft belongs to.</summary>
    public ClientInfoDto Client { get; private set; }

    /// <summary>The default model (web <c>model</c>), null = unset.</summary>
    public string? Model { get; set; }

    /// <summary>The pinned subscription account, "" = the provider's default.</summary>
    public string Account { get; set; } = "";

    /// <summary>The token written into the config.</summary>
    public string TokenId { get; set; } = "default";

    /// <summary>Claude Desktop's role map.</summary>
    public Dictionary<string, string> Roles { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Claude Code's extra model tiers.</summary>
    public Dictionary<string, string> Slots { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Client options (extras.options).</summary>
    public Dictionary<string, OptionValue> Options { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Per-model protocol overrides (extras.protocolOverrides).</summary>
    public Dictionary<string, string> Overrides { get; set; } = new(StringComparer.Ordinal);

    /// <summary>A draft equal to the saved state of <paramref name="client"/>.</summary>
    public ClientDraft(ClientInfoDto client)
    {
        Client = client;
        Sync(client);
    }

    /// <summary>Claude Desktop (role map instead of a default model).</summary>
    public bool IsDesktopClient => Client.Kind == "claude-desktop";

    /// <summary>Claude Code (model tiers, the direct panel).</summary>
    public bool IsClaudeCode => Client.Kind == "claude-code";

    /// <summary>The saved token (<c>tokenId ?? "default"</c>).</summary>
    public string SavedToken => Client.TokenId ?? "default";

    /// <summary>The option descriptors the server reports for the client.</summary>
    public IReadOnlyList<ClientOptionDescriptor> Descriptors => Client.Options ?? [];

    /// <summary>The protocols a model of this client can be given, its default first (ids).</summary>
    public IReadOnlyList<string> SupportedProtocols => (Client.Protocols ?? []).Select(Astra.Core.ApiProtocols.ToId).ToList();

    /// <summary>The models with a protocol choice (only when the client can use more than one protocol).</summary>
    public IReadOnlyList<ProtocolModel> ProtocolModels => SupportedProtocols.Count > 1 ? ClientExtras.ProtocolModelsOf(Client.Extras) : [];

    /// <summary>The Claude Code tiers that get a row of their own (the default model is the first slot).</summary>
    public IReadOnlyList<string> TierSlots => IsClaudeCode ? ClientExtras.ClaudeCodeModels.Where(s => s != "ANTHROPIC_MODEL").ToList() : [];

    /// <summary>At least one Claude Desktop role is set (enabling needs one).</summary>
    public bool HasRole => ClientExtras.HasRole(Roles);

    /// <summary>The account differs from the saved pin.</summary>
    public bool AccountChanged => Account != (Client.AccountId ?? "");

    /// <summary>The token differs from the saved one.</summary>
    public bool TokenChanged => TokenId != SavedToken;

    /// <summary>The roles differ from the saved ones.</summary>
    public bool RolesChanged => ClientExtras.RoleKey(Roles) != _savedRolesKey;

    /// <summary>The model differs from the saved one (Claude Code: the model or a tier).</summary>
    public bool ModelsChanged => IsClaudeCode
        ? Model != Client.SelectedModel || ClientExtras.SlotsKey(Slots) != _savedSlotsKey
        : Model != Client.SelectedModel;

    /// <summary>The options differ from the saved ones.</summary>
    public bool OptionsChanged => ClientExtras.OptionsKey(Options) != _savedOptionsKey;

    /// <summary>The protocol overrides differ from the saved ones.</summary>
    public bool OverridesChanged => ClientExtras.OverridesKey(Overrides) != _savedOverridesKey;

    /// <summary>
    /// The <c>extras</c> an enable / apply sends (web <c>extras</c>): Claude Desktop replaces the role map, Claude Code the tiers and options, a
    /// client with options or several protocols those; null keeps the stored extras.
    /// </summary>
    public JsonObject? Extras()
    {
        JsonObject Copy() => Client.Extras?.DeepClone() as JsonObject ?? new JsonObject();
        if (IsDesktopClient)
        {
            var e = Copy();
            e["roleMap"] = ClientExtras.StringJson(Roles);
            return e;
        }
        if (IsClaudeCode)
        {
            var e = Copy();
            e["models"] = ClientExtras.StringJson(Slots);
            e["options"] = ClientExtras.OptionsJson(Options);
            return e;
        }
        var hasOptions = Descriptors.Count > 0;
        var multi = SupportedProtocols.Count > 1;
        if (!hasOptions && !multi) return null;
        var x = Copy();
        if (hasOptions) x["options"] = ClientExtras.OptionsJson(Options);
        if (multi) x["protocolOverrides"] = ClientExtras.OverridesJson(Overrides);
        return x;
    }

    /// <summary>The body of preview-enable / enable (web <c>req</c>): providerId, model, tokenId and — when there are any — extras.</summary>
    public JsonObject EnableRequest()
    {
        var body = new JsonObject { ["providerId"] = Client.ProviderId, ["model"] = Model };
        if (Extras() is { } extras) body["extras"] = extras;
        body["tokenId"] = TokenId;
        return body;
    }

    /// <summary>
    /// The server sent a newer <paramref name="client"/>. Each field is reset only when its saved value (or the kind) changed, like the
    /// web's effects; a refetch that changes nothing keeps every edit.
    /// </summary>
    public void Sync(ClientInfoDto client)
    {
        var kindChanged = client.Kind != _kind;
        Client = client;
        if (kindChanged || client.SelectedModel != _savedModel) Model = client.SelectedModel;
        var accountKey = client.ProviderId + "\n" + client.AccountId;
        if (kindChanged || accountKey != _savedAccountKey) Account = client.AccountId ?? "";
        if (kindChanged || SavedToken != _savedToken) TokenId = SavedToken;
        var roles = ClientExtras.RolesOf(client.Extras);
        if (kindChanged || ClientExtras.RoleKey(roles) != _savedRolesKey) Roles = roles;
        var slots = ClientExtras.SlotsOf(client.Extras);
        if (kindChanged || ClientExtras.SlotsKey(slots) != _savedSlotsKey) Slots = slots;
        var options = ClientExtras.OptionsOf(client.Extras, client.Options);
        if (kindChanged || ClientExtras.OptionsKey(options) != _savedOptionsKey) Options = options;
        var overrides = ClientExtras.OverridesOf(client.Extras);
        if (kindChanged || ClientExtras.OverridesKey(overrides) != _savedOverridesKey) Overrides = overrides;

        _kind = client.Kind;
        _savedModel = client.SelectedModel;
        _savedAccountKey = accountKey;
        _savedToken = SavedToken;
        _savedRolesKey = ClientExtras.RoleKey(roles);
        _savedSlotsKey = ClientExtras.SlotsKey(slots);
        _savedOptionsKey = ClientExtras.OptionsKey(options);
        _savedOverridesKey = ClientExtras.OverridesKey(overrides);
        if (kindChanged)
        {
            _suggested = new(StringComparer.Ordinal);
            _suggestedFor = "\0";
        }
    }

    /// <summary>
    /// Claude Desktop without a saved role map: prefills one from the bound providers' models (local until enabling saves it). A later
    /// model list replaces only the previous suggestion, never a role the user picked (web <c>suggestedRoles</c> effect). True when the
    /// roles changed.
    /// </summary>
    public bool SuggestRoles(IReadOnlyList<string>? models)
    {
        if (!IsDesktopClient || Client.Enabled || ClientExtras.HasRole(ClientExtras.RolesOf(Client.Extras))) return false;
        var list = models ?? [];
        var signature = string.Join("\n", list);
        if (signature == _suggestedFor) return false;
        _suggestedFor = signature;
        var previous = _suggested;
        var next = ClientExtras.SuggestRoles(list);
        _suggested = next;
        var untouched = !ClientExtras.HasRole(Roles) || ClientExtras.RoleKey(Roles) == ClientExtras.RoleKey(previous);
        if (!untouched || ClientExtras.RoleKey(Roles) == ClientExtras.RoleKey(next)) return false;
        Roles = new Dictionary<string, string>(next, StringComparer.Ordinal);
        return true;
    }
}
