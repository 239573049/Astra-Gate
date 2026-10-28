using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Astra.Api.Contracts;
using Astra.Desktop.Core.Clients;
using Astra.Desktop.Core.I18n;
using static Astra.Desktop.Tests.Clients.ClientsTestData;

namespace Astra.Desktop.Tests.Clients;

public sealed class DiffLinesTests
{
    [Fact]
    public void Colours_and_numbers_follow_the_hunks()
    {
        var lines = DiffLines.Parse("--- a/x\n+++ b/x\n@@ -3,3 +3,4 @@\n a\n-b\n+c\n+d\n e\n");
        Assert.Equal(
            [DiffLineKind.FileHeader, DiffLineKind.FileHeader, DiffLineKind.Hunk, DiffLineKind.Context, DiffLineKind.Removed, DiffLineKind.Added, DiffLineKind.Added, DiffLineKind.Context, DiffLineKind.Context],
            lines.Select(l => l.Kind));
        Assert.Equal((3, 3), (lines[3].OldLine, lines[3].NewLine));
        Assert.Equal((4, (int?)null), (lines[4].OldLine, lines[4].NewLine));
        Assert.Equal(((int?)null, 4), (lines[5].OldLine, lines[5].NewLine));
        Assert.Equal((5, 6), (lines[7].OldLine, lines[7].NewLine));
        // The empty piece after the final newline is drawn but stands at no line.
        Assert.Equal(("", (int?)null), (lines[8].Text, lines[8].OldLine));
    }

    [Fact]
    public void Without_hunks_there_are_no_numbers()
    {
        var lines = DiffLines.Parse("+a\r\n-b");
        Assert.Equal([DiffLineKind.Added, DiffLineKind.Removed], lines.Select(l => l.Kind));
        Assert.Equal("+a", lines[0].Text);
        Assert.All(lines, l => Assert.Null(l.NewLine ?? l.OldLine));
        Assert.Single(DiffLines.Parse(null));
    }
}

public sealed class InstallLogicTests
{
    private static ClientInstallDto Inst(Action<JsonObject>? patch = null)
    {
        var o = new JsonObject
        {
            ["method"] = "npm", ["package"] = "x", ["homepageUrl"] = "https://x", ["installed"] = true, ["executable"] = "/bin/x", ["version"] = "1",
            ["latestVersion"] = "2", ["latestCheckedAt"] = null, ["latestError"] = null, ["updateAvailable"] = false, ["installCommand"] = null,
            ["updateCommand"] = null, ["updateVia"] = null, ["blocker"] = null, ["busy"] = false,
        };
        patch?.Invoke(o);
        return Read<ClientInstallDto>(o.ToJsonString());
    }

    private static ClientInstallJobDto Job(string state, string? hint = null, bool elevated = false, string action = "install")
        => new("x", action, state, "npm i -g x", DateTimeOffset.UnixEpoch, null, null, ["a"], hint, elevated);

    private static string T(string key, params (string Name, object? Value)[] args) => key + string.Concat(args.Select(a => "|" + a.Value));

    [Fact]
    public void Header_button()
    {
        Assert.Equal(InstallButton.None, InstallLogic.ButtonFor(null));
        Assert.Equal(InstallButton.Running, InstallLogic.ButtonFor(Inst(o => { o["busy"] = true; o["updateAvailable"] = true; })));
        Assert.Equal(InstallButton.Update, InstallLogic.ButtonFor(Inst(o => { o["updateAvailable"] = true; o["updateCommand"] = "u"; })));
        Assert.Equal(InstallButton.Shadowed, InstallLogic.ButtonFor(Inst(o => { o["updateAvailable"] = true; o["blocker"] = "shadowed"; })));
        Assert.Equal(InstallButton.Install, InstallLogic.ButtonFor(Inst(o => { o["installed"] = false; o["installCommand"] = "i"; })));
        Assert.Equal(InstallButton.Download, InstallLogic.ButtonFor(Inst(o => { o["installed"] = false; o["method"] = "manual"; })));
        Assert.Equal(InstallButton.None, InstallLogic.ButtonFor(Inst()));
    }

    [Fact]
    public void Texts()
    {
        Assert.Equal("clients.admin.tip", InstallLogic.AdminTipKey(Inst(o => { o["needsAdmin"] = true; o["adminAvailable"] = true; })));
        Assert.Equal("clients.admin.tipManual", InstallLogic.AdminTipKey(Inst(o => o["needsAdmin"] = true)));
        Assert.Null(InstallLogic.AdminTipKey(Inst()));
        Assert.Equal("clients.blocker.external-install|/bin/x", InstallLogic.BlockerText(Inst(o => o["blocker"] = "external-install"), T));
        Assert.Equal("clients.blocker.npm-missing", InstallLogic.BlockerText(Inst(o => o["blocker"] = "npm-missing"), T));
        Assert.Null(InstallLogic.BlockerText(Inst(), T));
        Assert.Equal("err", InstallLogic.LatestDetail(Inst(o => o["latestError"] = "err"), TimeSpan.Zero, "en", T));
        Assert.Equal("clients.checkedAt|10/26/2026, 12:00", InstallLogic.LatestDetail(Inst(o => o["latestCheckedAt"] = "2026-10-26T12:00:00Z"), TimeSpan.Zero, "en", T));
        var shadow = Inst(o => o["otherCopies"] = new JsonArray(new JsonObject { ["path"] = "/a", ["version"] = "1", ["newer"] = false }, new JsonObject { ["path"] = "/b", ["version"] = "3", ["newer"] = true }));
        Assert.Equal("/b", InstallLogic.Shadowing(shadow)!.Path);
    }

    [Fact]
    public void Sheet_state()
    {
        Assert.True(InstallLogic.Confirming(InstallSheetMode.Install, false, null));
        Assert.False(InstallLogic.Confirming(InstallSheetMode.Install, true, null));
        Assert.False(InstallLogic.Confirming(InstallSheetMode.Update, false, Job("running")));
        Assert.False(InstallLogic.Confirming(InstallSheetMode.Log, false, null));
        Assert.Equal(InstallFooter.Confirm, InstallLogic.FooterFor(true, null));
        Assert.Equal(InstallFooter.Running, InstallLogic.FooterFor(false, Job("running")));
        Assert.Equal(InstallFooter.Finished, InstallLogic.FooterFor(false, Job("failed")));
        Assert.Equal("clients.updateTitle", InstallLogic.TitleKey(InstallSheetMode.Update, null));
        Assert.Equal("clients.updateTitle", InstallLogic.TitleKey(InstallSheetMode.Log, Job("running", action: "update")));
        Assert.Equal("clients.installTitle", InstallLogic.TitleKey(InstallSheetMode.Log, null));
        Assert.Equal("npm i", InstallLogic.CommandFor(InstallSheetMode.Install, Inst(o => o["installCommand"] = "npm i")));
        Assert.Null(InstallLogic.CommandFor(InstallSheetMode.Log, Inst(o => o["installCommand"] = "npm i")));

        var admin = Inst(o => o["adminAvailable"] = true);
        Assert.True(InstallLogic.RetryAsAdmin(Job("failed", "permission-denied"), admin));
        Assert.False(InstallLogic.RetryAsAdmin(Job("failed", "permission-denied", elevated: true), admin));
        Assert.False(InstallLogic.RetryAsAdmin(Job("failed", "permission-denied"), Inst()));

        Assert.Equal("clients.job.notEffective", InstallLogic.StatusTitleKey(Job("succeeded", "shadowed")));
        Assert.Equal("clients.job.failed", InstallLogic.StatusTitleKey(Job("failed")));
        Assert.Equal("clients.hint.permission-deniedclients.admin.retryHint", InstallLogic.StatusBody(Job("failed", "permission-denied"), true, T));
        Assert.Equal("clients.admin.running", InstallLogic.StatusBody(Job("running", elevated: true), false, T));
        Assert.Null(InstallLogic.StatusBody(Job("succeeded"), false, T));
    }

    [Fact]
    public void Transition_announces_only_the_end_of_a_watched_run()
    {
        Assert.Equal(InstallOutcome.None, InstallLogic.Transition(null, Job("succeeded")));
        Assert.Equal(InstallOutcome.None, InstallLogic.Transition("running", Job("running")));
        Assert.Equal(InstallOutcome.Installed, InstallLogic.Transition("running", Job("succeeded")));
        Assert.Equal(InstallOutcome.Updated, InstallLogic.Transition("running", Job("succeeded", action: "update")));
        Assert.Equal(InstallOutcome.NotEffective, InstallLogic.Transition("running", Job("succeeded", "unchanged")));
        Assert.Equal(InstallOutcome.Ended, InstallLogic.Transition("running", Job("cancelled")));
        Assert.Equal(InstallOutcome.Uninstalled, InstallLogic.Transition("running", Job("succeeded", action: "uninstall")));
        Assert.Equal(InstallOutcome.NotEffective, InstallLogic.Transition("running", Job("succeeded", "still-installed", action: "uninstall")));
    }

    [Fact]
    public void Uninstall_row_offers_the_command_or_says_why_not()
    {
        var npm = Inst(o => o["uninstallCommand"] = "npm uninstall -g x");
        Assert.True(InstallLogic.ShowsUninstall(npm));
        Assert.Null(InstallLogic.UninstallUnavailableText(npm, T));
        Assert.Equal("clients.uninstallExternal|/bin/x", InstallLogic.UninstallUnavailableText(Inst(), T));
        Assert.Equal("clients.uninstallBuiltIn", InstallLogic.UninstallUnavailableText(Inst(o => o["method"] = "vscode-extension"), T));
        Assert.Equal("clients.uninstallManual", InstallLogic.UninstallUnavailableText(Inst(o => o["method"] = "manual"), T));
        Assert.Equal("clients.uninstallManual", InstallLogic.UninstallUnavailableText(Inst(o => o["method"] = "script"), T));
        var absent = Inst(o => o["installed"] = false);
        Assert.False(InstallLogic.ShowsUninstall(absent));
        Assert.Null(InstallLogic.UninstallUnavailableText(absent, T));
    }

    [Fact]
    public void Uninstall_sheet_state()
    {
        var inst = Inst(o => o["uninstallCommand"] = "npm uninstall -g x");
        Assert.Equal("npm uninstall -g x", InstallLogic.CommandFor(InstallSheetMode.Uninstall, inst));
        Assert.Equal("uninstall", InstallLogic.ActionName(InstallSheetMode.Uninstall));
        Assert.True(InstallLogic.Confirming(InstallSheetMode.Uninstall, false, null));
        Assert.False(InstallLogic.Confirming(InstallSheetMode.Uninstall, false, Job("running", action: "uninstall")));
        Assert.Equal("clients.uninstallTitle", InstallLogic.TitleKey(InstallSheetMode.Uninstall, null));
        Assert.Equal("clients.uninstallTitle", InstallLogic.TitleKey(InstallSheetMode.Log, Job("succeeded", action: "uninstall")));
        // Only an uninstall of a client Astra has taken over restores its config first.
        Assert.True(InstallLogic.RestoresFirst(InstallSheetMode.Uninstall, clientEnabled: true));
        Assert.False(InstallLogic.RestoresFirst(InstallSheetMode.Uninstall, clientEnabled: false));
        Assert.False(InstallLogic.RestoresFirst(InstallSheetMode.Update, clientEnabled: true));
        Assert.Equal("clients.hint.still-installed", InstallLogic.StatusBody(Job("succeeded", "still-installed", action: "uninstall"), false, T));
    }
}

public sealed class ClaudeDirectLogicTests
{
    private static ClaudeDirectAccountDto Account(string id, string status = "active", bool enabled = true, bool telemetry = false, string? email = null, string? plan = null)
        => new(id, "N-" + id, email, plan, status, enabled, telemetry);

    [Fact]
    public void Labels_and_selection()
    {
        Assert.Equal("N-a · me@x · Pro", ClaudeDirectLogic.Label(Account("a", email: "me@x", plan: "Pro"), "off"));
        Assert.Equal("N-b · off", ClaudeDirectLogic.Label(Account("b", "expired"), "off"));
        Assert.Equal("a · off", ClaudeDirectLogic.Label(new ClaudeDirectAccountDto("a", "", null, null, "active", false, false), "off"));

        var state = new ClaudeDirectStateDto("gateway", null, [Account("x", "expired"), Account("y")]);
        Assert.Equal("y", ClaudeDirectLogic.SelectedId(state));
        Assert.Equal("gateway", ClaudeDirectLogic.Mode(null));
        Assert.False(ClaudeDirectLogic.Warned(false, state));
        Assert.True(ClaudeDirectLogic.Warned(true, state));
        Assert.False(ClaudeDirectLogic.Warned(true, null));
        Assert.False(ClaudeDirectLogic.Live(state));
        Assert.True(ClaudeDirectLogic.Live(new ClaudeDirectStateDto("direct", "y", [Account("y", telemetry: true)])));
    }

    [Fact]
    public void Mode_switch()
    {
        var none = new ClaudeDirectStateDto("gateway", null, [Account("x", "expired")]);
        Assert.Equal((null, true), ClaudeDirectLogic.Switch(none, "direct", false));
        var ok = new ClaudeDirectStateDto("gateway", null, [Account("y")]);
        Assert.Equal(new ClaudeDirectSelectInput("direct", "y", null), ClaudeDirectLogic.Switch(ok, "direct", false).Body);
        Assert.Null(ClaudeDirectLogic.Switch(ok, "gateway", false).Body);
        Assert.Null(ClaudeDirectLogic.Switch(ok, "direct", true).Body);
        Assert.Null(ClaudeDirectLogic.Switch(null, "direct", false).Body);
    }

    [Fact]
    public void Requests_parse_and_total()
    {
        // The wire shape (camelCase) read through the contract the server now answers with.
        var rows = System.Text.Json.JsonSerializer.Deserialize("""
            [{"id":"r1","profileId":"p","sessionId":"s","model":"m","occurredAtUtc":"2026-10-26T11:00:00Z","status":"success","inputTokens":10,"outputTokens":5,"cacheReadTokens":100,"cacheCreationTokens":7,"durationMs":1200,"estimatedCostNanoUsd":2000000000},
             {"id":"r2","profileId":"p","sessionId":"s","model":"m","occurredAtUtc":"2026-10-26T10:00:00Z","status":"error","inputTokens":1,"outputTokens":0,"cacheReadTokens":0,"cacheCreationTokens":0,"durationMs":null,"estimatedCostNanoUsd":null}]
            """, Astra.Core.JsonContexts.Info<List<ClaudeDirectRequestDto>>(Astra.Core.Json.Api))!;
        Assert.Equal(2, rows.Count);
        var t = ClaudeDirectLogic.Totals(rows);
        Assert.Equal(new ClaudeDirectTotals(2, 11, 118, 5, 1, 2_000_000_000), t);
        Assert.Equal("$2.00", ClaudeDirectLogic.CostText(rows));
        Assert.Equal("—", ClaudeDirectLogic.CostText([rows[1]]));
        Assert.Equal("10/26/2026, 11:00 · tok|10|5 · 1200 ms",
            ClaudeDirectLogic.RowDetail(rows[0], TimeSpan.Zero, "en", (k, a) => "tok" + string.Concat(a.Select(x => "|" + x.Value))));
    }
}

/// <summary>Every <c>Loc.T("…")</c> literal key of the Clients page exists in the generated catalog (web messages.ts).</summary>
[Collection("Loc")]
public sealed partial class ClientsKeysTests
{
    [GeneratedRegex("Loc\\.T\\(\"([A-Za-z0-9_.\\-]+)\"")]
    private static partial Regex KeyLiteral();

    [Fact]
    public void Literal_keys_exist()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Astra.sln"))) dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        var files = new[] { "src/Astra.Desktop/Pages/Clients", "src/Astra.Desktop/Components/Clients" }
            .SelectMany(d => Directory.GetFiles(Path.Combine(dir!, d), "*.cs"));
        var missing = files.SelectMany(f => KeyLiteral().Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value))
            .Where(k => !k.EndsWith('.') && !Loc.Has(k)).Distinct().ToList();
        Assert.Empty(missing);
    }

    [Theory]
    [InlineData("clients.group.installed")]
    [InlineData("clients.group.notInstalled")]
    [InlineData("clients.group.comingSoon")]
    [InlineData("clients.option.context1m")]
    [InlineData("clients.option.context1m.hint")]
    [InlineData("clients.optionChoice.default")]
    [InlineData("clients.role.sonnet")]
    [InlineData("clients.modelSlot.ANTHROPIC_DEFAULT_OPUS_MODEL")]
    [InlineData("clients.job.running")]
    [InlineData("clients.method.npm")]
    [InlineData("clients.blocker.npm-missing")]
    [InlineData("clients.repair.problem.trailing-comma")]
    [InlineData("clients.hint.permission-denied")]
    [InlineData("providers.subscription.status.expired")]
    public void Composed_keys_exist(string key) => Assert.True(Loc.Has(key), key);
}
