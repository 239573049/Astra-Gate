using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Clients;
using Astra.Core.Models;
using Astra.Core.Routing;
using Astra.Core.Tokens;
using Astra.Data;
using Astra.Gateway.Pipeline;
using Microsoft.AspNetCore.Http;

namespace Astra.Gateway.Tests;

/// <summary>Provider-qualified model ids (issue #21): <c>model@alias</c> names the provider as well as the model.</summary>
public sealed class QualifiedModelTests
{
    private static Provider Named(string name, string? alias = null)
    {
        var p = new Provider { Id = Ulid.NewUlid(), Name = name };
        if (alias is not null) p.Settings[Provider.AliasKey] = alias;
        return p;
    }

    [Fact]
    public void Off_by_default_and_the_separator_is_checked()
    {
        Assert.Null(ModelQualifier.From(new AppSettings()));
        Assert.Equal("@", ModelQualifier.From(new AppSettings { ProviderQualifiedModels = true })!.Separator);
        Assert.Equal("::", ModelQualifier.From(new AppSettings { ProviderQualifiedModels = true, ModelProviderSeparator = "::" })!.Separator);
        // A separator that cannot be split on falls back to the default; the endpoint refuses it outright.
        Assert.Equal("@", ModelQualifier.Clean(" "));
        Assert.Equal("@", ModelQualifier.Clean(""));
        Assert.Equal("@", ModelQualifier.Clean("a b"));
        Assert.False(ModelQualifier.IsValidSeparator("a b"));
        Assert.True(ModelQualifier.IsValidSeparator("#"));
    }

    [Fact]
    public void Alias_is_the_setting_else_the_name_without_spaces()
    {
        Assert.Equal("zcode", Named("Z Code", "zcode").Alias());
        Assert.Equal("Work-Buddy-AI", Named("Work  Buddy AI").Alias());
        Assert.Equal("Z-Code", Named("Z Code", "   ").Alias());
        Assert.Equal("a-b", Named("x", " a b ").Alias());
    }

    [Fact]
    public void Splits_only_at_a_known_alias()
    {
        var q = new ModelQualifier("@");
        var zcode = Named("ZCode");
        var buddy = Named("Buddy", "wb");
        Provider[] providers = [zcode, buddy];

        Assert.True(q.TrySplit("GLM-5.3@ZCode", providers, out var model, out var hit));
        Assert.Equal("GLM-5.3", model);
        Assert.Same(zcode, hit);

        // Aliases match ignoring case; the alias, not the name, is what counts.
        Assert.True(q.TrySplit("glm-5.3@WB", providers, out model, out hit));
        Assert.Same(buddy, hit);
        Assert.False(q.TrySplit("glm-5.3@Buddy", providers, out _, out _));

        // Unknown alias, bare id, empty halves: left alone.
        Assert.False(q.TrySplit("glm-5.3@nobody", providers, out model, out _));
        Assert.Equal("glm-5.3@nobody", model);
        Assert.False(q.TrySplit("glm-5.3", providers, out _, out _));
        Assert.False(q.TrySplit("@ZCode", providers, out _, out _));
        Assert.False(q.TrySplit("glm@", providers, out _, out _));
    }

    [Fact]
    public void A_model_id_that_contains_the_separator_survives()
    {
        var q = new ModelQualifier("@");
        var p = Named("ZCode");
        // The last separator whose right side is a known alias splits; the earlier one belongs to the model id.
        Assert.True(q.TrySplit("vendor@model@ZCode", [p], out var model, out _));
        Assert.Equal("vendor@model", model);
        Assert.False(q.TrySplit("vendor@model", [p], out _, out _));
    }

    [Fact]
    public void An_alias_may_contain_the_separator()
    {
        var q = new ModelQualifier("@");
        var p = Named("x", "team@zcode");
        Assert.True(q.TrySplit("glm@team@zcode", [p], out var model, out var hit));
        Assert.Equal("glm", model);
        Assert.Same(p, hit);
    }

    [Fact]
    public void A_multi_character_separator_works()
    {
        var q = new ModelQualifier("::");
        var p = Named("ZCode");
        Assert.True(q.TrySplit("glm::ZCode", [p], out var model, out _));
        Assert.Equal("glm", model);
        Assert.False(q.TrySplit("glm@ZCode", [p], out _, out _));
    }

    [Fact]
    public async Task Listing_keeps_one_id_per_provider_only_when_asked()
    {
        var store = await RoutingStore.StartAsync("glm-5.3", "only-first");
        var second = await store.AddProviderAsync("Second", "glm-5.3", "only-second");
        var routing = new ClientRouting(store.Db, new EffectiveModelResolver(store.Db));
        RouteTarget[] targets = [new(store.Provider, null), new(second, null)];

        // Off: the id both serve is listed once, for the first provider.
        var plain = (await routing.ListModelsAsync(targets)).Select(m => m.Id).ToList();
        Assert.Equal(["glm-5.3", "only-first", "only-second"], plain.Order().ToList());

        // On: each provider lists its own, tagged with its alias.
        var qualified = await routing.ListModelsAsync(targets, new ModelQualifier("@"));
        Assert.Equal(
            ["glm-5.3@Models", "glm-5.3@Second", "only-first@Models", "only-second@Second"],
            qualified.Select(m => m.Id).Order().ToList());
        Assert.Equal(second.Id, qualified.Single(m => m.Id == "glm-5.3@Second").Target.Provider.Id);
        // The provider model behind it is still the real one, so capabilities and display names resolve as before.
        Assert.All(qualified, m => Assert.NotEqual(m.Id, m.Model.ModelId));
    }

    [Fact]
    public async Task A_mapped_id_is_qualified_too()
    {
        var store = await RoutingStore.StartAsync("glm-5.3");
        store.Provider.Settings[Provider.ModelMapKey] = new JsonObject { ["glm-latest"] = "glm-5.3" };
        await store.Db.Providers.UpdateAsync(store.Provider);
        var routing = new ClientRouting(store.Db, new EffectiveModelResolver(store.Db));
        var ids = (await routing.ListModelsAsync([new RouteTarget(store.Provider, null)], new ModelQualifier("@"))).Select(m => m.Id).Order().ToList();
        Assert.Equal(["glm-5.3@Models", "glm-latest@Models"], ids);
    }

    // ---- the routing seam ----

    private static GatewayRoute RouteOf(RouteTarget[] targets, string clientKind = "codex") =>
        new(new TokenRecord { Id = "t", Name = "t" }, new ClientRecord { Kind = clientKind, Enabled = true }, targets[0].AccountId, targets[0].Provider)
        { Targets = targets };

    private static AppSettings On(string separator = "@") => new() { ProviderQualifiedModels = true, ModelProviderSeparator = separator };

    [Fact]
    public async Task Off_leaves_the_route_alone()
    {
        var store = await RoutingStore.StartAsync("glm-5.3");
        var second = await store.AddProviderAsync("Second", "glm-5.3");
        var route = RouteOf([new(store.Provider, null), new(second, null)]);
        var after = await QualifiedModelRouting.ApplyAsync(new RouterOverRouting(store.Db), route, "glm-5.3@Second", new AppSettings(), false, default);
        Assert.Same(route, after);
    }

    [Fact]
    public async Task A_qualified_id_pins_the_provider_and_strips_the_alias()
    {
        var store = await RoutingStore.StartAsync("glm-5.3");
        var second = await store.AddProviderAsync("Second", "glm-5.3");
        var route = RouteOf([new(store.Provider, null), new(second, null)]);

        var after = await QualifiedModelRouting.ApplyAsync(new RouterOverRouting(store.Db), route, "glm-5.3@Second", On(), false, default);

        Assert.Equal(second.Id, after.Provider.Id);
        Assert.Single(after.Targets);
        Assert.Null(after.Pool);
        // The body keeps what the client sent; only the route knows the model and where it goes.
        Assert.Equal("glm-5.3", after.ClientModelFor("glm-5.3@Second"));
        Assert.Equal("glm-5.3", after.UpstreamModelFor("glm-5.3@Second"));
    }

    [Fact]
    public async Task A_bare_id_keeps_priority_routing_while_the_switch_is_on()
    {
        var store = await RoutingStore.StartAsync("glm-5.3");
        var second = await store.AddProviderAsync("Second", "glm-5.3");
        var route = RouteOf([new(store.Provider, null), new(second, null)]);
        var after = await QualifiedModelRouting.ApplyAsync(new RouterOverRouting(store.Db), route, "glm-5.3", On(), false, default);
        Assert.Same(route, after);
    }

    [Fact]
    public async Task The_mapping_of_the_chosen_provider_applies_to_the_stripped_id()
    {
        var store = await RoutingStore.StartAsync("real-model");
        store.Provider.Settings[Provider.ModelMapKey] = new JsonObject { ["alias-model"] = "real-model" };
        await store.Db.Providers.UpdateAsync(store.Provider);
        var route = RouteOf([new(store.Provider, null)]);
        var after = await QualifiedModelRouting.ApplyAsync(new RouterOverRouting(store.Db), route, "alias-model@Models", On(), false, default);
        Assert.Equal("real-model", after.UpstreamModelFor("alias-model@Models"));
    }

    [Fact]
    public async Task A_provider_that_does_not_serve_the_model_is_refused_in_chinese()
    {
        var store = await RoutingStore.StartAsync("glm-5.3");
        var second = await store.AddProviderAsync("Second", "other-model");
        var route = RouteOf([new(store.Provider, null), new(second, null)]);
        var e = await Assert.ThrowsAsync<GatewayException>(() =>
            QualifiedModelRouting.ApplyAsync(new RouterOverRouting(store.Db), route, "glm-5.3@Second", On(), false, default));
        Assert.Equal(404, e.Status);
        Assert.Contains("Second", e.Message);
        Assert.Contains("glm-5.3", e.Message);
    }

    [Fact]
    public async Task An_alias_outside_the_callers_targets_does_not_reach_that_provider()
    {
        var store = await RoutingStore.StartAsync("glm-5.3");
        var elsewhere = await store.AddProviderAsync("Elsewhere", "glm-5.3");
        var route = RouteOf([new(store.Provider, null)]);
        // Not one of this caller's providers, so the id is just an unknown model name: no split, no pin.
        var after = await QualifiedModelRouting.ApplyAsync(new RouterOverRouting(store.Db), route, "glm-5.3@Elsewhere", On(), false, default);
        Assert.Same(route, after);
        Assert.NotEqual(elsewhere.Id, after.Provider.Id);
    }

    /// <summary>The router's model check over a real <see cref="ClientRouting"/>; the other members are not used by the seam.</summary>
    private sealed class RouterOverRouting(AstraDatabase db) : IGatewayRouter
    {
        private readonly ClientRouting _routing = new(db, new EffectiveModelResolver(db));

        public async Task<(RouteTarget? Target, GradeProblem? Problem)> GradeUseAsync(
            GatewayRoute route, string? providerId, string modelId, bool isClaudeCode, CancellationToken ct)
        {
            var target = route.Targets.First(t => t.Provider.Id == providerId);
            return await _routing.MatchAsync([target], modelId, isClaudeCode, ct) is { } served
                ? (served, null)
                : (null, new GradeProblem(GradeProblems.ModelNotServed, target.Provider.Name));
        }

        public Task<GatewayRoute> ResolveAsync(HttpRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<GatewayRoute> ForModelAsync(GatewayRoute route, string modelId, bool isClaudeCode, CancellationToken ct) => throw new NotSupportedException();
        public Task<RouteTarget?> GradeTargetAsync(GatewayRoute route, string? providerId, string modelId, bool isClaudeCode, CancellationToken ct) => throw new NotSupportedException();
    }
}
