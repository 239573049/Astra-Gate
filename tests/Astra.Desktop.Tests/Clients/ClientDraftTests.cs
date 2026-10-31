using System.Text.Json.Nodes;
using Astra.Core.Clients;
using Astra.Desktop.Core.Clients;
using static Astra.Desktop.Tests.Clients.ClientsTestData;

namespace Astra.Desktop.Tests.Clients;

public sealed class ClientExtrasTests
{
    private static readonly ClientOptionDescriptor[] Descriptors =
    [
        new("context1m", "bool", "false", "gateway"),
        new("contextWindow", "int", "", "config"),
        new("cacheTtl", "enum", "default", "gateway", ["default", "5m", "1h"]),
    ];

    [Fact]
    public void Options_keep_known_non_default_values()
    {
        var extras = JsonNode.Parse("""{"options":{"context1m":true,"contextWindow":0,"cacheTtl":"1h","unknown":1}}""")!.AsObject();
        var o = ClientExtras.OptionsOf(extras, Descriptors);
        Assert.Equal(2, o.Count);
        Assert.Equal(new OptionValue.Bool(true), o["context1m"]);
        Assert.Equal(new OptionValue.Choice("1h"), o["cacheTtl"]);
        Assert.Empty(ClientExtras.OptionsOf(JsonNode.Parse("""{"options":{"cacheTtl":"2h","context1m":"yes"}}""")!.AsObject(), Descriptors));
        Assert.Empty(ClientExtras.OptionsOf(null, Descriptors));
        Assert.Equal(new OptionValue.Int(200000), ClientExtras.OptionsOf(JsonNode.Parse("""{"options":{"contextWindow":200000}}""")!.AsObject(), Descriptors)["contextWindow"]);
    }

    [Fact]
    public void Setting_a_default_removes_the_option()
    {
        var o = ClientExtras.SetOption(new Dictionary<string, OptionValue>(), "a", new OptionValue.Int(5));
        Assert.Single(o);
        Assert.Empty(ClientExtras.SetOption(o, "a", new OptionValue.Int(0)));
        Assert.Empty(ClientExtras.SetOption(o, "a", null));
        Assert.Empty(ClientExtras.SetOption(new Dictionary<string, OptionValue> { ["b"] = new OptionValue.Bool(true) }, "b", new OptionValue.Bool(false)));
        Assert.Empty(ClientExtras.SetOption(new Dictionary<string, OptionValue> { ["c"] = new OptionValue.Choice("1h") }, "c", new OptionValue.Choice("default")));
        Assert.Equal("a=5\nb=true", ClientExtras.OptionsKey(new Dictionary<string, OptionValue> { ["b"] = new OptionValue.Bool(true), ["a"] = new OptionValue.Int(5) }));
        Assert.Equal("""{"a":5,"b":true,"c":"1h"}""", ClientExtras.OptionsJson(new Dictionary<string, OptionValue> { ["a"] = new OptionValue.Int(5), ["b"] = new OptionValue.Bool(true), ["c"] = new OptionValue.Choice("1h") }).ToJsonString());
    }

    [Theory]
    [InlineData("12a3", 123L)]
    [InlineData("", null)]
    [InlineData("abc", null)]
    [InlineData(" 200 000 ", 200000L)]
    public void Int_option_keeps_digits_only(string text, long? expected) => Assert.Equal(expected, ClientExtras.ParseIntOption(text));

    [Fact]
    public void Protocol_models_and_overrides()
    {
        var extras = JsonNode.Parse("""
            {"models":{"a":{"id":"m1","name":"Model 1","protocols":["anthropic","bogus"]},"b":{"id":"m2"},"c":{"id":"m1"},"d":"x"},
             "protocolOverrides":{"m1":"gemini","m2":"nope"}}
            """)!.AsObject();
        var models = ClientExtras.ProtocolModelsOf(extras);
        Assert.Equal(["m1", "m2"], models.Select(m => m.Id));
        Assert.Equal("m2", models[1].Name);
        Assert.Equal(["anthropic"], models[0].Native);
        Assert.Equal(new Dictionary<string, string> { ["m1"] = "gemini" }, ClientExtras.OverridesOf(extras));

        string[] supported = ["openai-chat", "anthropic"];
        Assert.Equal(("anthropic", false), ClientExtras.AutoProtocol(models[0], supported));
        Assert.Equal(("openai-chat", true), ClientExtras.AutoProtocol(models[1], supported));

        var next = ClientExtras.SetOverride(new Dictionary<string, string> { ["m1"] = "gemini" }, "m2", "anthropic");
        Assert.Equal("m1=gemini\nm2=anthropic", ClientExtras.OverridesKey(next));
        Assert.Equal("m2=anthropic", ClientExtras.OverridesKey(ClientExtras.SetOverride(next, "m1", ClientExtras.AutoValue)));
    }

    [Fact]
    public void Slots_and_roles_trim_and_key()
    {
        var extras = JsonNode.Parse("""{"models":{"ANTHROPIC_MODEL":" a ","ANTHROPIC_DEFAULT_HAIKU_MODEL":"","X":"y"},"roleMap":{"opus":"o","sonnet":" "}}""")!.AsObject();
        var slots = ClientExtras.SlotsOf(extras);
        Assert.Equal(new Dictionary<string, string> { ["ANTHROPIC_MODEL"] = "a" }, slots);
        Assert.Equal("a\n\n\n\n", ClientExtras.SlotsKey(slots));
        var roles = ClientExtras.RolesOf(extras);
        Assert.Equal(new Dictionary<string, string> { ["opus"] = "o" }, roles);
        Assert.Equal("\no\n", ClientExtras.RoleKey(roles));
        Assert.True(ClientExtras.HasRole(roles));
    }

    [Fact]
    public void Suggested_roles_match_the_web()
    {
        Assert.Empty(ClientExtras.SuggestRoles([]));
        var claude = ClientExtras.SuggestRoles(["claude-sonnet-4-5", "claude-opus-4-1", "claude-haiku-4-5"]);
        Assert.Equal(("claude-sonnet-4-5", "claude-opus-4-1", "claude-haiku-4-5"), (claude["sonnet"], claude["opus"], claude["haiku"]));
        var other = ClientExtras.SuggestRoles(["gpt-4.1-mini", "gpt-5", "gemini-2.5-flash"]);
        Assert.Equal(("gpt-5", "gpt-5", "gpt-4.1-mini"), (other["sonnet"], other["opus"], other["haiku"]));
        var smallOnly = ClientExtras.SuggestRoles(["gpt-4o-mini"]);
        Assert.Equal(("gpt-4o-mini", "gpt-4o-mini", "gpt-4o-mini"), (smallOnly["sonnet"], smallOnly["opus"], smallOnly["haiku"]));
    }
}

public sealed class ClientDraftTests
{
    [Fact]
    public void Edits_survive_a_refetch_that_changes_nothing_and_reset_when_the_saved_value_moves()
    {
        var client = Client(patch: o => o["selectedModel"] = "a");
        var draft = new ClientDraft(client);
        Assert.Equal("a", draft.Model);
        Assert.Equal("default", draft.TokenId);
        draft.Model = "b";
        draft.TokenId = "t2";
        Assert.True(draft.ModelsChanged);
        Assert.True(draft.TokenChanged);

        draft.Sync(Client(patch: o => o["selectedModel"] = "a"));
        Assert.Equal("b", draft.Model);
        Assert.Equal("t2", draft.TokenId);

        draft.Sync(Client(patch: o => { o["selectedModel"] = "c"; o["tokenId"] = "t9"; }));
        Assert.Equal("c", draft.Model);
        Assert.Equal("t9", draft.TokenId);
        Assert.False(draft.ModelsChanged);
    }

    [Fact]
    public void Plain_client_sends_no_extras()
    {
        var draft = new ClientDraft(Client(patch: o => o["selectedModel"] = "m"));
        Assert.Null(draft.Extras());
        Assert.Equal("""{"providerId":"p1","model":"m","tokenId":"default"}""", draft.EnableRequest().ToJsonString());
    }

    [Fact]
    public void Options_and_protocols_go_into_extras()
    {
        var draft = new ClientDraft(Client("zed", o =>
        {
            o["extras"] = new JsonObject { ["keep"] = 1, ["options"] = new JsonObject { ["context1m"] = true } };
            o["options"] = new JsonArray(new JsonObject { ["key"] = "context1m", ["type"] = "bool", ["default"] = "false", ["scope"] = "gateway" });
            o["protocols"] = new JsonArray("openai-chat", "anthropic");
        }));
        Assert.Equal(["openai-chat", "anthropic"], draft.SupportedProtocols);
        draft.Overrides = new Dictionary<string, string> { ["m"] = "anthropic" };
        Assert.True(draft.OverridesChanged);
        Assert.False(draft.OptionsChanged);
        Assert.Equal("""{"keep":1,"options":{"context1m":true},"protocolOverrides":{"m":"anthropic"}}""", draft.Extras()!.ToJsonString());
    }

    [Fact]
    public void Claude_code_sends_tiers_and_options()
    {
        var draft = new ClientDraft(Client("claude-code", o => o["extras"] = new JsonObject { ["models"] = new JsonObject { ["ANTHROPIC_DEFAULT_OPUS_MODEL"] = "o" } }));
        Assert.Equal(["ANTHROPIC_DEFAULT_FABLE_MODEL", "ANTHROPIC_DEFAULT_OPUS_MODEL", "ANTHROPIC_DEFAULT_SONNET_MODEL", "ANTHROPIC_DEFAULT_HAIKU_MODEL"], draft.TierSlots);
        draft.Slots = new Dictionary<string, string> { ["ANTHROPIC_DEFAULT_OPUS_MODEL"] = "o2" };
        Assert.True(draft.ModelsChanged);
        Assert.Equal("""{"models":{"ANTHROPIC_DEFAULT_OPUS_MODEL":"o2"},"options":{}}""", draft.Extras()!.ToJsonString());
    }

    [Fact]
    public void Claude_desktop_suggests_roles_once_and_never_over_a_pick()
    {
        var draft = new ClientDraft(Client("claude-desktop"));
        Assert.False(draft.HasRole);
        Assert.True(draft.SuggestRoles(["claude-sonnet-4-5", "claude-haiku-4-5"]));
        Assert.Equal("claude-sonnet-4-5", draft.Roles["sonnet"]);
        Assert.False(draft.SuggestRoles(["claude-sonnet-4-5", "claude-haiku-4-5"]));

        // A new model list replaces our own suggestion...
        Assert.True(draft.SuggestRoles(["gpt-5", "gpt-5-mini"]));
        Assert.Equal("gpt-5", draft.Roles["sonnet"]);
        // ...but not a role the user picked.
        draft.Roles = new Dictionary<string, string>(draft.Roles) { ["opus"] = "mine" };
        Assert.False(draft.SuggestRoles(["x-large"]));
        Assert.Equal("mine", draft.Roles["opus"]);
        Assert.Equal("""{"roleMap":{"sonnet":"gpt-5","opus":"mine","haiku":"gpt-5-mini"}}""", draft.Extras()!.ToJsonString());

        // An enabled client or one with a saved map gets no suggestion.
        Assert.False(new ClientDraft(Client("claude-desktop", o => o["enabled"] = true)).SuggestRoles(["a"]));
        Assert.False(new ClientDraft(Client("claude-desktop", o => o["extras"] = new JsonObject { ["roleMap"] = new JsonObject { ["opus"] = "x" } })).SuggestRoles(["a"]));
    }

    [Fact]
    public void Account_change_tracks_the_saved_pin()
    {
        var draft = new ClientDraft(Client(patch: o => o["accountId"] = "a1"));
        Assert.Equal("a1", draft.Account);
        draft.Account = "";
        Assert.True(draft.AccountChanged);
        draft.Sync(Client(patch: o => o["accountId"] = "a2"));
        Assert.Equal("a2", draft.Account);
        Assert.False(draft.AccountChanged);
    }
}
