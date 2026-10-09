using System.Text.Json.Nodes;
using Astra.Gateway.Pipeline;

namespace Astra.Gateway.Tests;

/// <summary>
/// Claude Code 身份伪装（subscription.mimic_claude_code，见 <see cref="ClaudeCodeMimicry"/>）：
/// 请求体改写成 CLI 的 3 块 system + metadata.user_id，请求头换成 claude-cli 的指纹。
/// 指纹黄金值用同一算法独立计算后钉死，防止无意的公式漂移。
/// </summary>
public class ClaudeCodeMimicryTests
{
    [Fact]
    public void Body_Carries_The_Three_Block_System_And_The_Metadata_Identity()
    {
        const string body = """{"model":"claude-sonnet-4-5","max_tokens":16,"messages":[{"role":"user","content":"hi"}]}""";

        var transformed = JsonNode.Parse(ClaudeCodeMimicry.ApplyBody(body, "acc-1", null))!.AsObject();

        var system = transformed["system"]!.AsArray();
        Assert.Equal(3, system.Count);
        // 第一块是计费归属头，带 cc 指纹；第二块是身份；第三块带缓存断点。
        Assert.StartsWith($"x-anthropic-billing-header: cc_version={ClaudeCodeMimicry.CliVersion}.",
            system[0]!["text"]!.GetValue<string>());
        Assert.EndsWith("; cc_entrypoint=cli;", system[0]!["text"]!.GetValue<string>());
        Assert.Equal("You are Claude Code, Anthropic's official CLI for Claude.", system[1]!["text"]!.GetValue<string>());
        Assert.Equal("ephemeral", system[2]!["cache_control"]!["type"]!.GetValue<string>());

        // metadata.user_id 是 JSON 字符串形态（CLI ≥ 2.1.78），device_id 是 64 位十六进制。
        var meta = JsonNode.Parse(transformed["metadata"]!["user_id"]!.GetValue<string>())!.AsObject();
        Assert.Matches("^[0-9a-f]{64}$", meta["device_id"]!.GetValue<string>());
        Assert.Equal("", meta["account_uuid"]!.GetValue<string>()); // 未知名账号宁缺勿错
        Assert.Matches("^[0-9a-f-]{36}$", meta["session_id"]!.GetValue<string>());
        Assert.Single(transformed["messages"]!.AsArray()); // 原本没有 system，不往 messages 里塞
    }

    [Fact]
    public void Body_Moves_The_Caller_System_Into_The_Message_History()
    {
        const string body = """{"model":"claude-sonnet-4-5","max_tokens":16,"system":"Be terse.","messages":[{"role":"user","content":"hi"}]}""";

        var transformed = JsonNode.Parse(ClaudeCodeMimicry.ApplyBody(body, "acc-1", "sess-9"))!.AsObject();

        var messages = transformed["messages"]!.AsArray();
        Assert.Equal(3, messages.Count);
        Assert.Equal("user", messages[0]!["role"]!.GetValue<string>());
        Assert.Contains("[System Instructions]\nBe terse.", messages[0]!["content"]![0]!["text"]!.GetValue<string>());
        Assert.Equal("assistant", messages[1]!["role"]!.GetValue<string>());
        // 客户端带的会话 id 优先于派生值。
        var meta = JsonNode.Parse(transformed["metadata"]!["user_id"]!.GetValue<string>())!.AsObject();
        Assert.Equal("sess-9", meta["session_id"]!.GetValue<string>());
    }

    [Fact]
    public void Body_Is_Passed_Through_Unchanged_When_It_Is_Not_An_Object()
    {
        Assert.Equal("not json", ClaudeCodeMimicry.ApplyBody("not json", "acc-1", null));
    }

    [Fact]
    public void Fingerprint_Matches_The_Reference_Derivation()
    {
        // 黄金值独立按 sub2api 的公式（salt + 第 4/7/20 字节，缺位补 '0' + 版本）算出。
        Assert.Equal("a99", ClaudeCodeMimicry.CcFingerprint("Hello, world!"));
        Assert.Equal("d4a", ClaudeCodeMimicry.CcFingerprint("hi"));
        // 确定性：同输入同指纹；设备 id 与会话 id 也一样。
        Assert.Equal(ClaudeCodeMimicry.CcFingerprint("Hello, world!"), ClaudeCodeMimicry.CcFingerprint("Hello, world!"));
        Assert.Equal(ClaudeCodeMimicry.DeviceId("acc-1"), ClaudeCodeMimicry.DeviceId("acc-1"));
        Assert.NotEqual(ClaudeCodeMimicry.DeviceId("acc-1"), ClaudeCodeMimicry.DeviceId("acc-2"));
        Assert.Equal(ClaudeCodeMimicry.SessionUuid("seed"), ClaudeCodeMimicry.SessionUuid("seed"));
    }

    [Fact]
    public void Headers_Replace_The_Caller_Identity_With_The_Pinned_Claude_Code_Set()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://upstream.test/v1/messages");
        request.Headers.TryAddWithoutValidation("User-Agent", "opencode/1.0");
        request.Headers.TryAddWithoutValidation("anthropic-beta", "context-1m-2025-08-07");
        request.Headers.TryAddWithoutValidation("anthropic-version", "2020-01-01");

        ClaudeCodeMimicry.ApplyHeaders(request, "acc-1", "sess-1");

        // .NET 的 UA 集合只承载单个 product token（见 ClaudeOAuthHeaders.SetUserAgent）。
        Assert.Equal("claude-cli/2.1.281", request.Headers.UserAgent.ToString());
        Assert.Equal("cli", request.Headers.TryGetValues("x-app", out var app) ? string.Join(",", app) : "");
        Assert.Equal("js", request.Headers.TryGetValues("x-stainless-lang", out var lang) ? string.Join(",", lang) : "");
        Assert.NotNull(request.Headers.TryGetValues("x-stainless-os", out var os) ? os : null);
        // 完整 beta 集，客户端的 beta 不混进来。
        Assert.Equal(string.Join(',', ClaudeCodeMimicry.Betas), request.Headers.TryGetValues("anthropic-beta", out var beta) ? string.Join(",", beta) : "");
        Assert.Equal(12, ClaudeCodeMimicry.Betas.Length);
        Assert.Equal("2023-06-01", request.Headers.TryGetValues("anthropic-version", out var version) ? string.Join(",", version) : "");
        // .NET 的头解析器把 ";q=0.9" 规范化成 "; q=0.9"（HTTP 语义等价），线上就是这个形态。
        Assert.Equal("en-US,en; q=0.9", request.Headers.TryGetValues("Accept-Language", out var locale) ? string.Join(",", locale) : "");
        Assert.Equal("sess-1", request.Headers.TryGetValues("x-claude-code-session-id", out var session) ? string.Join(",", session) : "");
    }
}
