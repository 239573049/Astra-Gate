using Astra.Api.Contracts;
using Astra.Desktop.Core.Mcp;

namespace Astra.Desktop.Tests.Mcp;

/// <summary>The "your own MCP servers" sheet's logic (issue #34): the text boxes and the body they become.</summary>
public sealed class LocalMcpDraftTests
{
    private static LocalMcpServerDto Server(string transport = "stdio") => new(
        "id1", "files", transport, transport == "stdio" ? "npx" : "", transport == "stdio" ? ["-y", "srv"] : [], ["API_KEY"],
        transport == "http" ? "https://d.example/mcp" : "", ["Authorization"], true, ["claude-code"], []);

    [Fact]
    public void Arguments_are_one_per_line_without_blank_lines()
        => Assert.Equal(["-y", "@mcp/files", "/Users/me/my docs"], LocalMcpDraft.ParseArgs("-y\n@mcp/files\r\n\n  \n/Users/me/my docs\n"));

    [Fact]
    public void Environment_lines_keep_an_equals_sign_inside_the_value_and_reject_a_line_without_a_name()
    {
        var env = LocalMcpDraft.ParseEnv("API_KEY=sk-1\nURL=https://x/?a=b\n\n")!;
        Assert.Equal("sk-1", env["API_KEY"]);
        Assert.Equal("https://x/?a=b", env["URL"]);
        Assert.Empty(LocalMcpDraft.ParseEnv("")!);
        Assert.Null(LocalMcpDraft.ParseEnv("=oops"));
        Assert.Null(LocalMcpDraft.ParseEnv("no equals sign"));
    }

    [Fact]
    public void Header_lines_split_at_the_first_colon_and_need_a_name()
    {
        var headers = LocalMcpDraft.ParseHeaders("Authorization: Bearer a:b\nX-Id:7\n")!;
        Assert.Equal("Bearer a:b", headers["Authorization"]);
        Assert.Equal("7", headers["X-Id"]);
        Assert.Null(LocalMcpDraft.ParseHeaders("no colon"));
        Assert.Null(LocalMcpDraft.ParseHeaders(": value"));
    }

    [Fact]
    public void A_new_stdio_draft_needs_a_name_and_a_command_and_sends_its_environment()
    {
        var draft = new LocalMcpDraft();
        Assert.False(draft.IsValid);
        draft.Name = " files ";
        draft.Command = " npx ";
        draft.Args = "-y\nsrv";
        draft.Env = "K=v";
        Assert.True(draft.IsValid);

        var body = draft.ToBody();
        Assert.Equal("files", body.Name);
        Assert.Equal("stdio", body.Transport);
        Assert.Equal("npx", body.Command);
        Assert.Equal(["-y", "srv"], body.Args);
        Assert.Equal("v", body.Env!["K"]);
        Assert.Null(body.Url);
        Assert.Null(body.Headers);
        Assert.Equal(["claude-code", "codex", "gemini-cli", "qwen-code", "opencode"], body.Clients);
    }

    [Fact]
    public void A_bad_environment_line_makes_the_draft_invalid()
    {
        var draft = new LocalMcpDraft { Name = "a", Command = "x", Env = "oops" };
        Assert.False(draft.IsValid);
    }

    [Fact]
    public void An_existing_draft_starts_blank_and_blank_environment_and_headers_keep_what_is_stored()
    {
        var stdio = new LocalMcpDraft(Server());
        Assert.Equal("", stdio.Env);
        Assert.Equal("-y\nsrv", stdio.Args);
        Assert.Null(stdio.ToBody().Env);            // null = keep

        stdio.Env = "NEW=1";
        Assert.Equal("1", stdio.ToBody().Env!["NEW"]);

        var http = new LocalMcpDraft(Server("http"));
        var body = http.ToBody();
        Assert.Equal("http", body.Transport);
        Assert.Equal("https://d.example/mcp", body.Url);
        Assert.Null(body.Headers);                  // null = keep
        Assert.Null(body.Command);
        Assert.Equal(["claude-code"], body.Clients);
    }

    [Fact]
    public void Only_the_chosen_clients_are_sent_in_the_clients_own_order()
    {
        var draft = new LocalMcpDraft { Name = "a", Command = "x" };
        draft.ChosenClients.Clear();
        draft.ChosenClients.Add("opencode");
        draft.ChosenClients.Add("codex");
        Assert.Equal(["codex", "opencode"], draft.ToBody().Clients);
    }

    [Fact]
    public void A_row_summarises_the_command_line_or_the_address_and_asks_for_attention_only_for_a_wanted_client_that_is_not_applied()
    {
        Assert.Equal("npx -y srv", LocalMcpDraft.Summary(Server()));
        Assert.Equal("https://d.example/mcp", LocalMcpDraft.Summary(Server("http")));
        var ok = Server() with { Placements = [new("claude-code", true, "applied", "f", null), new("codex", false, "missing", "f", null)] };
        Assert.False(LocalMcpDraft.NeedsAttention(ok));
        Assert.True(LocalMcpDraft.NeedsAttention(ok with { Placements = [new("codex", true, "drifted", "f", null)] }));
        Assert.Equal("Gemini CLI", LocalMcpDraft.ClientName("gemini-cli"));
    }
}
