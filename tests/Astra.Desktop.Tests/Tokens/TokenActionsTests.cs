using System.Net;
using System.Text.Json.Nodes;
using Astra.Api.Contracts;
using Astra.Core;
using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.Tokens;
using Astra.Desktop.Tests.Overview;

namespace Astra.Desktop.Tests.Tokens;

public sealed class TokenActionsTests
{
    private static string Json(TokenDto token) => JsonContextsApi.Serialize(token);

    private static (TokenActions Actions, StubHandler Server, RecordingFeedback Feedback, QueryClient Queries) Make(Func<HttpRequestMessage, (HttpStatusCode, string)> answer)
    {
        var server = new StubHandler(answer);
        var api = new ApiClient("http://fixture", server);
        var feedback = new RecordingFeedback();
        var queries = new QueryClient();
        return (new TokenActions(api, queries, feedback, Fakes.T), server, feedback, queries);
    }

    private static TokenDto Token(string id = "tok1", string name = "ci-bot", bool enabled = true, bool isDefault = false) => Dtos.Token(id, name, isDefault, enabled);

    [Fact]
    public async Task Create_posts_the_trimmed_name_and_the_direct_provider_and_toasts()
    {
        var (actions, server, feedback, _) = Make(_ => (HttpStatusCode.OK, Json(Token())));
        var made = await actions.CreateAsync("  ci-bot  ", "prov1");
        Assert.Equal("ci-bot", made!.Name);
        var seen = Assert.Single(server.Seen);
        Assert.Equal(("POST", "/api/tokens", "1"), (seen.Method, seen.Url, seen.Admin));
        Assert.Equal("""{"name":"ci-bot","providerId":"prov1"}""", seen.Body);
        Assert.Equal([("success", "tokens.created(name=ci-bot)", null)], feedback.Toasts);
    }

    [Fact]
    public async Task Create_without_a_provider_sends_null_and_an_error_toasts_without_a_token()
    {
        var (ok, server, _, _) = Make(_ => (HttpStatusCode.OK, Json(Token())));
        await ok.CreateAsync("x", "");
        Assert.Equal("""{"name":"x","providerId":null}""", server.Seen[0].Body);

        var (failing, _, feedback, _) = Make(_ => (HttpStatusCode.BadRequest, """{"error":"名称已存在"}"""));
        Assert.Null(await failing.CreateAsync("x", null));
        Assert.Equal([("error", "common.error", "名称已存在")], feedback.Toasts);
    }

    [Fact]
    public async Task Update_patches_the_token_and_reports_success_or_the_error()
    {
        var (actions, server, feedback, _) = Make(_ => (HttpStatusCode.OK, Json(Token())));
        Assert.True(await actions.UpdateAsync("a/b", new JsonObject { ["enabled"] = false }, "saved"));
        Assert.Equal(("PATCH", "/api/tokens/a%2Fb", """{"enabled":false}"""), (server.Seen[0].Method, server.Seen[0].Url, server.Seen[0].Body));
        Assert.Equal([("success", "saved", null)], feedback.Toasts);

        var (failing, _, f2, _) = Make(_ => (HttpStatusCode.Conflict, """{"error":"重复的限额"}"""));
        Assert.False(await failing.UpdateAsync("t", new JsonObject()));
        Assert.Equal([("error", "common.error", "重复的限额")], f2.Toasts);
    }

    [Fact]
    public async Task Disabling_asks_first_and_stops_when_declined_but_enabling_does_not_ask()
    {
        var (actions, server, feedback, _) = Make(_ => (HttpStatusCode.OK, Json(Token())));
        feedback.Answer = false;
        await actions.ToggleAsync(Token(enabled: true));
        Assert.Empty(server.Seen);
        var ask = Assert.Single(feedback.Confirms);
        Assert.Equal(("tokens.disableConfirm(name=ci-bot)", "tokens.disableDetail", "tokens.disable", true), (ask.Title, ask.Message, ask.ConfirmText, ask.Destructive));

        feedback.Answer = true;
        await actions.ToggleAsync(Token(enabled: true));
        Assert.Equal("""{"enabled":false}""", server.Seen.Single().Body);

        feedback.Confirms.Clear();
        server.Seen.Clear();
        await actions.ToggleAsync(Token(enabled: false));
        Assert.Empty(feedback.Confirms);
        Assert.Equal("""{"enabled":true}""", server.Seen.Single().Body);
    }

    [Fact]
    public async Task Reset_confirms_then_reports_the_rewritten_and_the_skipped_clients()
    {
        var body = """{"token":null,"rewritten":["codex","claude-code"],"skipped":["zed","plugin-x"]}""";
        var (actions, server, feedback, _) = Make(_ => (HttpStatusCode.OK, body));
        await actions.ResetAsync(Token());
        Assert.Equal(("POST", "/api/tokens/tok1/reset"), (server.Seen[0].Method, server.Seen[0].Url));
        Assert.Equal(
            [("success", "tokens.resetDone(count=2)", null), ("error", "common.error", "tokens.skipped(clients=Zed, x)")],
            feedback.Toasts.Select(t => (t.Kind, t.Title, t.Description)));
    }

    [Fact]
    public async Task A_declined_reset_or_delete_sends_nothing()
    {
        var (actions, server, feedback, _) = Make(_ => (HttpStatusCode.OK, "{}"));
        feedback.Answer = false;
        await actions.ResetAsync(Token());
        await actions.DeleteAsync(Token());
        Assert.Empty(server.Seen);
        Assert.Equal(2, feedback.Confirms.Count);
        Assert.All(feedback.Confirms, c => Assert.True(c.Destructive));
    }

    [Fact]
    public async Task Delete_removes_the_token_and_toasts_its_name()
    {
        var (actions, server, feedback, _) = Make(_ => (HttpStatusCode.OK, """{"token":null,"rewritten":[],"skipped":[]}"""));
        await actions.DeleteAsync(Token());
        Assert.Equal(("DELETE", "/api/tokens/tok1"), (server.Seen[0].Method, server.Seen[0].Url));
        Assert.Equal("1", server.Seen[0].Admin);
        Assert.Equal([("success", "tokens.deleted(name=ci-bot)", null)], feedback.Toasts);
    }

    [Fact]
    public async Task Copying_the_key_reveals_it_only_now_and_hands_it_to_the_clipboard()
    {
        var (actions, server, feedback, _) = Make(_ => (HttpStatusCode.OK, """{"token":"sk-astra-secret"}"""));
        string? copied = null;
        await actions.CopyKeyAsync("tok1", text =>
        {
            copied = text;
            return Task.FromResult(true);
        });
        Assert.Equal("sk-astra-secret", copied);
        Assert.Equal(("POST", "/api/tokens/tok1/reveal"), (server.Seen[0].Method, server.Seen[0].Url));
        Assert.Equal([("success", "tokens.copied", null)], feedback.Toasts);
        // The secret never reaches a toast.
        Assert.DoesNotContain(feedback.Toasts, t => (t.Title + t.Description).Contains("secret"));
    }

    [Fact]
    public async Task A_clipboard_that_refuses_and_a_failed_reveal_are_reported()
    {
        var (actions, _, feedback, _) = Make(_ => (HttpStatusCode.OK, """{"token":"sk-astra-secret"}"""));
        await actions.CopyKeyAsync("tok1", _ => Task.FromResult(false));
        Assert.Equal([("error", "common.error", "tokens.copyFailed")], feedback.Toasts);

        var (failing, _, f2, _) = Make(_ => (HttpStatusCode.NotFound, """{"error":"令牌不存在"}"""));
        var called = false;
        await failing.CopyKeyAsync("gone", _ =>
        {
            called = true;
            return Task.FromResult(true);
        });
        Assert.False(called);
        Assert.Equal([("error", "common.error", "令牌不存在")], f2.Toasts);
    }

    [Fact]
    public async Task Copying_an_address_toasts_the_given_text()
    {
        var (actions, server, feedback, _) = Make(_ => (HttpStatusCode.OK, "{}"));
        string? copied = null;
        await actions.CopyTextAsync("http://x/v1", "tokens.urlCopied", t =>
        {
            copied = t;
            return Task.FromResult(true);
        });
        Assert.Equal("http://x/v1", copied);
        Assert.Empty(server.Seen);
        Assert.Equal([("success", "tokens.urlCopied", null)], feedback.Toasts);
    }

    [Fact]
    public async Task A_mutation_makes_the_token_and_client_lists_stale()
    {
        var (actions, _, _, queries) = Make(_ => (HttpStatusCode.OK, Json(Token())));
        var tokens = queries.Query<List<TokenDto>>(QueryKeys.Tokens, _ => Task.FromResult(new List<TokenDto>()));
        var clients = queries.Query<List<int>>(QueryKeys.Clients, _ => Task.FromResult(new List<int>()));
        await tokens.RefetchAsync();
        await clients.RefetchAsync();
        Assert.False(tokens.IsStale);
        Assert.False(clients.IsStale);
        await actions.UpdateAsync("tok1", new JsonObject());
        Assert.True(tokens.IsStale);
        Assert.True(clients.IsStale);
    }
}

/// <summary>Serializes a DTO the way the admin API does (camelCase).</summary>
internal static class JsonContextsApi
{
    public static string Serialize<T>(T value) => Astra.Core.Json.SerializeApi(value);
}
