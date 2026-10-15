using System.Net.Http;
using Astra.Gateway.Pipeline;

namespace Astra.Gateway.Tests;

public class ProviderHealthTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void A_Rate_Limit_Cools_Until_The_Reset_Capped_At_Thirty_Minutes()
    {
        var clock = new Clock();
        var health = new ProviderHealth(clock);
        health.Report("p", ProviderOutcome.RateLimited, clock.Now.AddMinutes(2));
        Assert.False(health.IsAvailable("p"));
        Assert.Equal(ProviderHealthStates.Cooling, health.Status("p").State);
        Assert.Equal(clock.Now.AddMinutes(2), health.AvailableAt("p"));

        clock.Now = clock.Now.AddMinutes(2);
        Assert.True(health.TryAdmit("p", out var probe));
        Assert.Null(probe);
        Assert.Equal(ProviderHealthStates.Healthy, health.Status("p").State);

        health.Report("p", ProviderOutcome.RateLimited, clock.Now.AddDays(3));
        Assert.Equal(clock.Now + ProviderHealth.MaxRateLimitCooldown, health.AvailableAt("p"));
        health.Report("q", ProviderOutcome.RateLimited);
        Assert.Equal(clock.Now + ProviderHealth.DefaultRateLimitCooldown, health.AvailableAt("q"));
    }

    [Fact]
    public void Rejected_Credentials_Cool_For_Ten_Minutes()
    {
        var clock = new Clock();
        var health = new ProviderHealth(clock);
        health.Report("p", ProviderOutcome.AuthRejected);
        Assert.Equal(clock.Now + ProviderHealth.AuthCooldown, health.AvailableAt("p"));
        Assert.Equal("auth_rejected", health.Status("p").Reason);
    }

    [Fact]
    public void Three_Failures_Open_The_Circuit_And_A_Probe_Decides()
    {
        var clock = new Clock();
        var health = new ProviderHealth(clock);
        health.Report("p", ProviderOutcome.ServerError);
        health.Report("p", ProviderOutcome.Unreachable);
        Assert.True(health.IsAvailable("p"));
        health.Report("p", ProviderOutcome.Timeout);
        Assert.False(health.IsAvailable("p"));
        Assert.Equal(ProviderHealthStates.Open, health.Status("p").State);
        Assert.Equal(clock.Now + ProviderHealth.BreakerBase, health.AvailableAt("p"));

        // A late failure of a request admitted before the circuit opened changes nothing.
        health.Report("p", ProviderOutcome.ServerError);
        Assert.Equal(clock.Now + ProviderHealth.BreakerBase, health.AvailableAt("p"));

        clock.Now += ProviderHealth.BreakerBase;
        Assert.Equal(ProviderHealthStates.HalfOpen, health.Status("p").State);
        Assert.True(health.TryAdmit("p", out var probe));
        Assert.NotNull(probe);
        // One probe at a time.
        Assert.False(health.TryAdmit("p", out _));

        // The probe fails: open again for twice as long.
        health.Report("p", ProviderOutcome.ServerError);
        Assert.Equal(clock.Now + ProviderHealth.BreakerBase * 2, health.AvailableAt("p"));

        clock.Now += ProviderHealth.BreakerBase * 2;
        Assert.True(health.TryAdmit("p", out var second));
        second!.Dispose();
        // An unused probe slot is free again.
        Assert.True(health.TryAdmit("p", out var third));
        health.Report("p", ProviderOutcome.Success);
        third!.Dispose();
        Assert.Equal(ProviderHealthStates.Healthy, health.Status("p").State);
        Assert.Equal(0, health.Status("p").ConsecutiveFailures);
    }

    [Fact]
    public void The_Backoff_Is_Capped_And_A_Stuck_Probe_Frees_Its_Slot()
    {
        var clock = new Clock();
        var health = new ProviderHealth(clock);
        for (var i = 0; i < 3; i++) health.Report("p", ProviderOutcome.ServerError);
        for (var i = 0; i < 10; i++)
        {
            clock.Now = health.AvailableAt("p")!.Value;
            Assert.True(health.TryAdmit("p", out _));
            health.Report("p", ProviderOutcome.ServerError);
        }
        Assert.Equal(clock.Now + ProviderHealth.BreakerMax, health.AvailableAt("p"));

        clock.Now = health.AvailableAt("p")!.Value;
        Assert.True(health.TryAdmit("p", out _));
        Assert.False(health.TryAdmit("p", out _));
        clock.Now += ProviderHealth.ProbeTimeout;
        Assert.True(health.TryAdmit("p", out _));
    }

    [Fact]
    public void A_Neutral_Answer_Resets_The_Failure_Count()
    {
        var health = new ProviderHealth(new Clock());
        health.Report("p", ProviderOutcome.ServerError);
        health.Report("p", ProviderOutcome.ServerError);
        health.Report("p", ProviderOutcome.Neutral);
        health.Report("p", ProviderOutcome.ServerError);
        Assert.True(health.IsAvailable("p"));
    }

    [Theory]
    [InlineData(200, false, ProviderOutcome.Success)]
    [InlineData(400, false, ProviderOutcome.Neutral)]
    [InlineData(413, false, ProviderOutcome.Neutral)]
    [InlineData(422, false, ProviderOutcome.Neutral)]
    [InlineData(401, true, ProviderOutcome.AuthRejected)]
    [InlineData(403, true, ProviderOutcome.AuthRejected)]
    [InlineData(404, true, ProviderOutcome.Success)]
    [InlineData(408, true, ProviderOutcome.Timeout)]
    [InlineData(429, true, ProviderOutcome.RateLimited)]
    [InlineData(500, true, ProviderOutcome.ServerError)]
    [InlineData(503, true, ProviderOutcome.ServerError)]
    [InlineData(529, true, ProviderOutcome.ServerError)]
    public void Statuses_Are_Classified(int status, bool failOver, ProviderOutcome outcome) =>
        Assert.Equal(new AttemptVerdict(failOver, outcome), ProviderFailover.Classify(status));

    [Fact]
    public void Exceptions_Are_Classified_Unless_The_Client_Left()
    {
        Assert.Equal(new AttemptVerdict(true, ProviderOutcome.Unreachable), ProviderFailover.Classify(new HttpRequestException("refused"), CancellationToken.None));
        Assert.Equal(new AttemptVerdict(true, ProviderOutcome.Timeout),
            ProviderFailover.Classify(new TaskCanceledException("connect", new TimeoutException()), CancellationToken.None));
        Assert.Equal(new AttemptVerdict(true, ProviderOutcome.AuthRejected),
            ProviderFailover.Classify(new GatewayException(401, "authentication_error", "x"), CancellationToken.None));
        Assert.Null(ProviderFailover.Classify(new GatewayException(400, "invalid_request_error", "x"), CancellationToken.None));
        Assert.Null(ProviderFailover.Classify(new InvalidOperationException(), CancellationToken.None));

        using var gone = new CancellationTokenSource();
        gone.Cancel();
        Assert.Null(ProviderFailover.Classify(new HttpRequestException("x"), gone.Token));
    }
}
