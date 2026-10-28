using Astra.Api.Contracts;
using Astra.Desktop.Core.Tokens;
using Astra.Desktop.Tests.Overview;

namespace Astra.Desktop.Tests.Tokens;

public sealed class TokenPresentationTests
{
    private static TokenBudgetUsageDto Usage(string metric, string period, decimal limit, decimal used, double percent, DateTimeOffset? resets = null)
        => new(metric, period, limit, used, limit - used, percent, used >= limit, resets);

    [Theory]
    [InlineData(12.6, 13, LimitTone.Accent)]
    [InlineData(69.4, 69, LimitTone.Accent)]
    [InlineData(69.5, 70, LimitTone.Warning)]
    [InlineData(89.4, 89, LimitTone.Warning)]
    [InlineData(89.5, 90, LimitTone.Danger)]
    [InlineData(150, 100, LimitTone.Danger)]
    [InlineData(-3, 0, LimitTone.Accent)]
    public void The_limit_bar_clamps_the_percent_and_runs_hotter_towards_the_cap(double usedPercent, int percent, LimitTone tone)
    {
        var bar = TokenPresentation.LimitBar(Usage("requests", "month", 5000, 412, usedPercent), TimeSpan.Zero, "zh", Fakes.T);
        Assert.Equal((percent, tone), (bar.Percent, bar.Tone));
    }

    [Fact]
    public void A_limit_bar_formats_cost_as_money_and_counts_compactly_and_names_the_reset()
    {
        var reset = new DateTimeOffset(2026, 10, 27, 0, 0, 0, TimeSpan.Zero);
        var cost = TokenPresentation.LimitBar(Usage("cost", "day", 5m, 0.63m, 12.6, reset), TimeSpan.Zero, "zh", Fakes.T);
        Assert.Equal("tokens.period.day · tokens.metric.cost", cost.Label);
        Assert.Equal("tokens.limitUsed(used=$0.63,limit=$5.00)", cost.UsedText);
        Assert.Equal("tokens.limitResets(time=2026/10/27 00:00)", cost.ResetText);
        var tokens = TokenPresentation.LimitBar(Usage("tokens", "total", 5_000_000m, 1_234_567m, 24.7), TimeSpan.Zero, "en", Fakes.T);
        Assert.Equal("tokens.limitUsed(used=1.2M,limit=5M)", tokens.UsedText);
        Assert.Null(tokens.ResetText);
    }

    [Fact]
    public void Rpm_text_and_unlimited()
    {
        var capped = Dtos.Token(limits: new TokenLimitsDto([], 60), recent: 3);
        Assert.Equal("tokens.rpm · tokens.rpmUsage(used=3,limit=60)", TokenPresentation.RpmText(capped, Fakes.T));
        Assert.False(TokenPresentation.IsUnlimited(capped));
        Assert.Null(TokenPresentation.RpmText(Dtos.Token(), Fakes.T));
        Assert.True(TokenPresentation.IsUnlimited(Dtos.Token()));
        Assert.False(TokenPresentation.IsUnlimited(Dtos.Token(usage: [Usage("cost", "day", 1, 0, 0)])));
    }

    [Fact]
    public void Allowed_clients_any_direct_only_or_named()
    {
        Assert.Equal("tokens.allowedClientsAny", TokenPresentation.AllowedClientsText(Dtos.Token(), Fakes.T));
        Assert.Equal("tokens.directOnly", TokenPresentation.AllowedClientsText(Dtos.Token(allowed: []), Fakes.T));
        Assert.Equal("Codex, Claude Code", TokenPresentation.AllowedClientsText(Dtos.Token(allowed: ["codex", "claude-code"]), Fakes.T));
        Assert.Equal("tokens.noClients", TokenPresentation.ClientsTitle([], Fakes.T));
        Assert.Equal("Zed", TokenPresentation.ClientsTitle(["zed"], Fakes.T));
    }

    [Fact]
    public void The_direct_provider_line_prefers_the_name_then_the_id_then_none()
    {
        var providers = new[] { Dtos.Provider("p1", "OpenAI") };
        Assert.Equal("OpenAI", TokenPresentation.DirectProviderText(Dtos.Token(providerId: "p1"), providers, Fakes.T));
        Assert.Equal("deleted", TokenPresentation.DirectProviderText(Dtos.Token(providerId: "deleted"), providers, Fakes.T));
        Assert.Equal("tokens.directNone", TokenPresentation.DirectProviderText(Dtos.Token(), providers, Fakes.T));
    }

    [Fact]
    public void The_stats_table_has_today_and_total_rows()
    {
        var token = Dtos.Token() with
        {
            Today = Dtos.Stats(4.82m, 31, 912_000, 118_000, 0.662, 62.4),
            Total = Dtos.Stats(318.4m, 2214, 61_200_000, 7_900_000, null, null),
        };
        var rows = TokenPresentation.StatsTable(token, Fakes.T);
        Assert.Equal(["tokens.today", "tokens.total"], rows.Select(r => r.Label));
        Assert.Equal(("$4.82", "31", "1M", "912K / 118K", "66.2%", "62.4 tok/s"), (rows[0].Cost, rows[0].Requests, rows[0].Tokens, rows[0].InOut, rows[0].CacheHit, rows[0].Tps));
        Assert.Equal(("$318.40", "2,214", "69.1M", "61.2M / 7.9M", "—", "—"), (rows[1].Cost, rows[1].Requests, rows[1].Tokens, rows[1].InOut, rows[1].CacheHit, rows[1].Tps));
    }

    [Fact]
    public void Pool_badge_and_members()
    {
        var pool = new TokenPoolDto("round_robin", "session", true, 3,
        [
            new TokenPoolMemberDto("p1", null, 1, 1, false, new PoolMemberHealthDto("healthy", null, null)),
            new TokenPoolMemberDto("gone", null, 1, 1, true, new PoolMemberHealthDto("missing", null, null)),
        ]);
        Assert.Equal("tokens.pool.summary(count=2,strategy=tokens.strategy.round_robin)", TokenPresentation.PoolBadge(pool, Fakes.T));
        var members = TokenPresentation.PoolMembers(pool, [Dtos.Provider("p1", "OpenAI")], Fakes.Noon, TimeSpan.Zero, "zh", Fakes.T);
        Assert.Equal(("OpenAI", HealthTone.Green, "tokens.health.healthy"), (members[0].Name, members[0].Tone, members[0].HealthText));
        Assert.Equal(("gone", HealthTone.Red), (members[1].Name, members[1].Tone));
    }

    [Fact]
    public void Provider_rows_take_the_name_from_the_provider_list_and_the_share_from_requests()
    {
        var stats = new[]
        {
            new ProviderStatDto("p1", "old name", 233, 217, 0.93, 27.23m, 0, 0, 1320, 0, 1),
            new ProviderStatDto("p2", null, 169, 150, 0.89, 11.67m, 0, 0, null, 1, 0),
            new ProviderStatDto("p3", "Logged", 0, 0, 0, 0m, 0, 0, null, 0, 0),
        };
        var rows = TokenPresentation.ProviderRows(stats, [Dtos.Provider("p1", "Anthropic")]);
        Assert.Equal(["Anthropic", "p2", "Logged"], rows.Select(r => r.Name));
        Assert.Equal(233.0 / 402, rows[0].Share, 12);
        Assert.Equal(0, rows[2].Share);
        Assert.Equal((1L, 0L), (rows[0].In, rows[0].Out));
        Assert.Equal(0, TokenPresentation.ProviderRows([new ProviderStatDto("x", null, 0, 0, 0, 0m, 0, 0, null, 0, 0)], [])[0].Share);
    }

    [Fact]
    public void Log_rows_and_urls()
    {
        Assert.Equal("http://127.0.0.1:17321/v1", TokenPresentation.V1Url("http://127.0.0.1:17321"));
        Assert.Equal("npx @aidotnet/astra-connect join https://gate.example.com", TokenPresentation.JoinCommand("https://gate.example.com"));
        Assert.Equal("cost:day", TokenPresentation.LimitKey(Usage("cost", "day", 1, 0, 0)));
    }
}
