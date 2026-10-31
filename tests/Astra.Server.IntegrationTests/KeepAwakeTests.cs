using System.Diagnostics;
using System.Net;
using System.Text;
using Astra.Core;
using Astra.Core.Clients;
using Astra.Core.Models;
using Astra.Core.Tokens;
using Astra.Gateway.Pipeline;
using Astra.Server.Api;
using Astra.Server.Power;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Astra.Server.IntegrationTests;

/// <summary>
/// Keeping the system awake while gateway requests are in flight (AppSettings.PreventSleep, issue #14): the take / grace /
/// let-go state machine, the setting through the admin API, the middleware's count of a streamed request, and the inhibitors.
/// </summary>
public class KeepAwakeTests
{
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(30);

    private static KeepAwakeService Service(FakeInhibitor inhibitor, ManualClock clock, Func<bool> enabled) =>
        new(enabled, inhibitor, clock, Grace, NullLogger<KeepAwakeService>.Instance);

    // ---------------------------------------------------------------- state machine

    [Fact]
    public void Off_By_Default_Nothing_Is_Taken_But_Requests_Are_Counted()
    {
        var (inhibitor, clock) = (new FakeInhibitor(), new ManualClock());
        var service = Service(inhibitor, clock, () => new AppSettings().PreventSleep);

        service.Enter();
        Assert.Equal(1, service.Active);
        service.Exit();
        clock.Advance(Grace);

        Assert.Equal(0, inhibitor.Acquired);
        Assert.False(service.Held);
        Assert.Equal(0, clock.Pending);
    }

    [Fact]
    public void The_First_Request_Takes_It_And_It_Is_Let_Go_A_Grace_Period_After_The_Last()
    {
        var (inhibitor, clock) = (new FakeInhibitor(), new ManualClock());
        var service = Service(inhibitor, clock, () => true);

        service.Enter();
        service.Enter();
        Assert.True(service.Held);
        Assert.Equal(1, inhibitor.Acquired);
        Assert.Equal(KeepAwakeService.Reason, inhibitor.Reason);

        service.Exit();
        Assert.Equal(0, clock.Pending); // one still in flight: no grace timer yet
        service.Exit();
        Assert.Equal(1, clock.Pending);

        clock.Advance(Grace - TimeSpan.FromSeconds(1));
        Assert.True(service.Held);
        Assert.True(inhibitor.Held);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(service.Held);
        Assert.False(inhibitor.Held);
        Assert.Equal(1, inhibitor.Released);
        Assert.Equal(0, clock.Pending);
    }

    [Fact]
    public void A_Request_Within_The_Grace_Period_Keeps_It_And_Starts_The_Wait_Again()
    {
        var (inhibitor, clock) = (new FakeInhibitor(), new ManualClock());
        var service = Service(inhibitor, clock, () => true);

        service.Enter();
        service.Exit();
        clock.Advance(TimeSpan.FromSeconds(20));
        service.Enter();
        Assert.Equal(0, clock.Pending);
        clock.Advance(TimeSpan.FromSeconds(20)); // past the first grace period: still busy
        Assert.True(service.Held);
        service.Exit();
        clock.Advance(TimeSpan.FromSeconds(29));
        Assert.True(service.Held);
        clock.Advance(TimeSpan.FromSeconds(1));

        Assert.False(service.Held);
        Assert.Equal(1, inhibitor.Acquired);
        Assert.Equal(1, inhibitor.Released);
    }

    [Fact]
    public void A_Grace_Timer_That_Fired_As_It_Was_Cancelled_Lets_Nothing_Go()
    {
        var (inhibitor, clock) = (new FakeInhibitor(), new ManualClock());
        var service = Service(inhibitor, clock, () => true);

        service.Enter();
        service.Exit();
        var stale = clock.Timers.Single();
        service.Enter(); // cancels the timer, but its callback may already be running on a pool thread
        stale.InvokeAnyway();

        Assert.True(service.Held);
        Assert.Equal(0, inhibitor.Released);
    }

    [Fact]
    public void Turning_It_Off_Lets_Go_At_Once_And_On_Takes_It_Mid_Request()
    {
        var (inhibitor, clock) = (new FakeInhibitor(), new ManualClock());
        var on = true;
        var service = Service(inhibitor, clock, () => on);

        service.Enter();
        Assert.True(service.Held);
        on = false;
        service.Refresh();
        Assert.False(service.Held);
        Assert.Equal(1, inhibitor.Released);

        on = true;
        service.Refresh(); // still in flight
        Assert.True(service.Held);
        Assert.Equal(2, inhibitor.Acquired);

        service.Exit();
        on = false;
        service.Refresh(); // during the grace period
        Assert.False(service.Held);
        Assert.Equal(0, clock.Pending);
    }

    [Fact]
    public void A_Setting_Turned_Off_Without_A_Refresh_Is_Seen_On_The_Next_Change()
    {
        var (inhibitor, clock) = (new FakeInhibitor(), new ManualClock());
        var on = true;
        var service = Service(inhibitor, clock, () => on);

        service.Enter();
        on = false;
        service.Exit();

        Assert.False(service.Held);
        Assert.Equal(0, clock.Pending);
    }

    [Fact]
    public async Task Stopping_The_Host_Lets_Go_And_Takes_Nothing_More()
    {
        var (inhibitor, clock) = (new FakeInhibitor(), new ManualClock());
        var service = Service(inhibitor, clock, () => true);

        service.Enter();
        await service.StopAsync(CancellationToken.None);
        Assert.False(service.Held);
        Assert.Equal(1, inhibitor.Released);

        service.Enter(); // a request still draining
        service.Exit();
        Assert.Equal(1, inhibitor.Acquired);
        Assert.Equal(0, clock.Pending);
    }

    [Fact]
    public void An_Inhibitor_That_Fails_Never_Fails_The_Request()
    {
        var (inhibitor, clock) = (new FakeInhibitor { Unavailable = true }, new ManualClock());
        var service = Service(inhibitor, clock, () => true);
        service.Enter();
        Assert.False(service.Held);
        service.Exit();
        Assert.Equal(0, clock.Pending);

        var throwing = new FakeInhibitor { Throw = new DllNotFoundException("IOKit") };
        var other = Service(throwing, clock, () => true);
        other.Enter();
        other.Exit();
        Assert.False(other.Held);
    }

    // ---------------------------------------------------------------- middleware

    [Theory]
    [InlineData("POST", "/v1/chat/completions", true)]
    [InlineData("POST", "/v1/responses", true)]
    [InlineData("POST", "/v1/messages", true)]
    [InlineData("POST", "/v1/messages/count_tokens", true)]
    [InlineData("POST", "/v1beta/models/gemini-2.5-pro:streamGenerateContent", true)]
    [InlineData("GET", "/v1/models", false)]
    [InlineData("GET", "/v1beta/models", false)]
    [InlineData("POST", "/v1/astra/key", false)]
    [InlineData("PATCH", "/api/settings", false)]
    [InlineData("POST", "/api/providers/p/test", false)]
    public void Only_Gateway_Posts_Count(string method, string path, bool counts)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        Assert.Equal(counts, KeepAwakeMiddleware.IsGatewayWork(context.Request));
    }

    [Fact]
    public async Task The_Setting_Round_Trips_And_A_Streamed_Request_Counts_Until_Its_Stream_Ends()
    {
        var (inhibitor, clock) = (new FakeInhibitor(), new ManualClock());
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // The conversation streams until released; anything else the gateway asks the upstream gets an empty answer.
        var upstream = new FakeUpstream(seen => seen.Url.AbsolutePath.EndsWith("/chat/completions", StringComparison.Ordinal)
            ? GatedSse(release.Task) : FakeUpstream.Json("""{"data":[]}"""));
        await using var host = await TestHost.StartAsync(b =>
        {
            b.Services.AddHttpClient(ProviderProbe.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => upstream);
            b.Services.AddSingleton<ISleepInhibitor>(inhibitor);
            b.Services.AddSingleton(sp => new KeepAwakeService(() => sp.GetRequiredService<SettingsService>().Current.PreventSleep,
                sp.GetRequiredService<ISleepInhibitor>(), clock, Grace, sp.GetRequiredService<ILogger<KeepAwakeService>>()));
        });
        var key = await DirectProviderAsync(host, upstream);
        var keepAwake = host.App.Services.GetRequiredService<KeepAwakeService>();

        // Off by default, then on through the admin API.
        Assert.False((await host.GetJsonAsync("/api/settings"))["preventSleep"]!.GetValue<bool>());
        var (status, body) = await host.SendAsync(HttpMethod.Patch, "/api/settings", new { preventSleep = true });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(body!["preventSleep"]!.GetValue<bool>());
        Assert.True((await host.GetJsonAsync("/api/settings"))["preventSleep"]!.GetValue<bool>());
        Assert.Equal(0, keepAwake.Active); // admin calls are not gateway work
        Assert.Equal(0, inhibitor.Acquired);

        // A streamed request holds it while the upstream keeps its stream open.
        using (var response = await StreamAsync(host, key))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(1, keepAwake.Active);
            Assert.True(inhibitor.Held);
            release.SetResult();
            var text = await response.Content.ReadAsStringAsync();
            Assert.Contains("[DONE]", text);
        }
        await WaitAsync(() => keepAwake.Active == 0);
        Assert.True(inhibitor.Held); // the grace period
        clock.Advance(Grace);
        Assert.False(inhibitor.Held);
        Assert.Equal(1, inhibitor.Released);

        // Turning it off through the API lets go at once, even mid-request.
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        release = second;
        using (var response = await StreamAsync(host, key))
        {
            Assert.True(inhibitor.Held);
            Assert.Equal(2, inhibitor.Acquired);
            (status, body) = await host.SendAsync(HttpMethod.Patch, "/api/settings", new { preventSleep = false });
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.False(body!["preventSleep"]!.GetValue<bool>());
            Assert.False(inhibitor.Held);
            Assert.Equal(1, keepAwake.Active);
            second.SetResult();
            await response.Content.ReadAsStringAsync();
        }
        await WaitAsync(() => keepAwake.Active == 0);
        Assert.Equal(2, inhibitor.Acquired);
        Assert.Equal(0, clock.Pending);
    }

    /// <summary>A provider on the fake upstream as the default token's direct-call provider; returns the bare token.</summary>
    private static async Task<string> DirectProviderAsync(TestHost host, FakeUpstream upstream)
    {
        host.App.Services.GetRequiredService<GatewayHttpClients>().HandlerFactory = _ => upstream;
        var protector = host.App.Services.GetRequiredService<ISecretProtector>();
        var provider = new Provider
        {
            Id = Ulid.NewUlid(),
            Name = "Fake upstream",
            AuthScheme = AuthSchemes.Bearer,
            ApiKeyEnc = protector.Protect("sk-upstream-secret"),
            Endpoints = [new ProviderEndpoint { Protocol = ApiProtocol.OpenAIChat, BaseUrl = GatewayFixture.BaseUrlFor(ApiProtocol.OpenAIChat) }],
            PreferredUpstreamProtocols = [ApiProtocol.OpenAIChat],
        };
        await host.Db.Providers.InsertAsync(provider);
        var token = (await host.Db.Tokens.GetAsync(TokenIds.Default))!;
        token.ProviderId = provider.Id;
        await host.Db.Tokens.UpdateAsync(token);
        return protector.Unprotect(token.KeyEnc!);
    }

    private static async Task<HttpResponseMessage> StreamAsync(TestHost host, string key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = new StringContent("""{"model":"gpt-5","messages":[{"role":"user","content":"hi"}],"stream":true}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {key}");
        return await host.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        for (var i = 0; i < 250 && !condition(); i++) await Task.Delay(20);
        Assert.True(condition());
    }

    private static HttpResponseMessage GatedSse(Task release)
    {
        const string head = "data: {\"id\":\"c1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"gpt-5\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"O\"},\"finish_reason\":null}]}\n\n";
        const string tail = "data: {\"id\":\"c1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"gpt-5\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"K\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":3,\"completion_tokens\":2,\"total_tokens\":5}}\n\n" +
                            "data: [DONE]\n\n";
        var content = new StreamContent(new GatedStream(head, tail, release));
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    /// <summary>Sends <c>head</c>, waits for <c>release</c>, then sends <c>tail</c> and ends.</summary>
    private sealed class GatedStream(string head, string tail, Task release) : Stream
    {
        private ReadOnlyMemory<byte> _pending = Encoding.UTF8.GetBytes(head);
        private bool _tailSent;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_pending.IsEmpty)
            {
                if (_tailSent) return 0;
                await release.WaitAsync(cancellationToken);
                (_pending, _tailSent) = (Encoding.UTF8.GetBytes(tail), true);
            }
            var count = Math.Min(buffer.Length, _pending.Length);
            _pending[..count].CopyTo(buffer);
            _pending = _pending[count..];
            return count;
        }

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // ---------------------------------------------------------------- inhibitors

    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void The_Systemd_Inhibitor_Holds_The_Lock_Until_It_Closes_The_Pipe()
    {
        if (OperatingSystem.IsWindows()) return; // a shell script stands in for systemd-inhibit
        using var dir = new TempDir();
        var args = Path.Combine(dir.Path, "args");
        var ended = Path.Combine(dir.Path, "ended");
        // Like systemd-inhibit: record the options (whole, then renamed into place), run the command (cat on Astra's pipe),
        // and end when it does.
        var script = dir.Script("systemd-inhibit", $"printf '%s\\n' \"$@\" > '{args}.tmp'\nmv '{args}.tmp' '{args}'\nshift 4\n\"$@\"\necho eof > '{ended}'\n");
        var inhibitor = new SystemdSleepInhibitor(script, NullLogger.Instance);

        Assert.True(inhibitor.Acquire(KeepAwakeService.Reason));
        Assert.True(inhibitor.Acquire(KeepAwakeService.Reason)); // held: nothing new started
        Assert.True(SpinWait.SpinUntil(() => File.Exists(args), TimeSpan.FromSeconds(5)));
        Assert.Equal(["--what=idle:sleep", "--who=Astra", "--why=" + KeepAwakeService.Reason, "--mode=block", "cat"],
            File.ReadAllLines(args));
        Assert.False(File.Exists(ended));

        inhibitor.Release();
        // cat read end-of-file and ended on its own: a kill would have taken the script down before it wrote "ended".
        Assert.True(SpinWait.SpinUntil(() => File.Exists(ended), TimeSpan.FromSeconds(5)));
        Assert.False(inhibitor.Unavailable);
        inhibitor.Release(); // a no-op now
    }

    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void The_Systemd_Inhibitor_Gives_Up_When_It_Cannot_Take_The_Lock()
    {
        if (OperatingSystem.IsWindows()) return;
        using var dir = new TempDir();
        var script = dir.Script("systemd-inhibit", "echo 'Failed to connect to bus: No such file or directory' >&2\nexit 1\n");
        var inhibitor = new SystemdSleepInhibitor(script, NullLogger.Instance);

        Assert.True(inhibitor.Acquire(KeepAwakeService.Reason)); // started; it fails a moment later
        Assert.True(SpinWait.SpinUntil(() => inhibitor.Unavailable, TimeSpan.FromSeconds(5)));
        inhibitor.Release();
        Assert.False(inhibitor.Acquire(KeepAwakeService.Reason));

        var missing = new SystemdSleepInhibitor(null, NullLogger.Instance);
        Assert.False(missing.Acquire(KeepAwakeService.Reason));
        Assert.True(missing.Unavailable);
        missing.Release();
    }

    /// <summary>The real IOKit assertion: it shows in <c>pmset -g assertions</c> while held and is gone after.</summary>
    [MacOSFact]
    public void The_Mac_Inhibitor_Takes_A_Real_Power_Assertion()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var reason = $"{KeepAwakeService.Reason} (test {Guid.NewGuid():N})";
        var inhibitor = new MacSleepInhibitor(NullLogger.Instance);
        try
        {
            Assert.True(inhibitor.Acquire(reason));
            var held = Pmset();
            Assert.Contains(reason, held);
            Assert.Contains(MacSleepInhibitor.AssertionType, held.Split('\n').Single(l => l.Contains(reason, StringComparison.Ordinal)));
        }
        finally
        {
            inhibitor.Release();
        }
        Assert.DoesNotContain(reason, Pmset());
    }

    private static string Pmset()
    {
        using var process = Process.Start(new ProcessStartInfo("pmset", "-g assertions") { RedirectStandardOutput = true, UseShellExecute = false })!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output;
    }

    // ---------------------------------------------------------------- fakes

    private sealed class FakeInhibitor : ISleepInhibitor
    {
        private int _acquired, _released, _held;

        public bool Unavailable { get; init; }
        public Exception? Throw { get; init; }
        public string? Reason { get; private set; }
        public int Acquired => Volatile.Read(ref _acquired);
        public int Released => Volatile.Read(ref _released);
        public bool Held => Volatile.Read(ref _held) == 1;

        public bool Acquire(string reason)
        {
            if (Throw is not null) throw Throw;
            if (Unavailable) return false;
            Reason = reason;
            Interlocked.Increment(ref _acquired);
            Volatile.Write(ref _held, 1);
            return true;
        }

        public void Release()
        {
            Interlocked.Increment(ref _released);
            Volatile.Write(ref _held, 0);
        }
    }

    /// <summary>A clock whose one-shot timers fire only when <see cref="Advance"/> passes their due time.</summary>
    private sealed class ManualClock : TimeProvider
    {
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _now = new(2026, 10, 30, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        /// <summary>Every timer created so far, cancelled or not.</summary>
        public IReadOnlyList<ManualTimer> Timers
        {
            get
            {
                lock (_timers) return _timers.ToList();
            }
        }

        /// <summary>Timers neither fired nor cancelled.</summary>
        public int Pending => Timers.Count(t => !t.Done);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state, _now + dueTime);
            lock (_timers) _timers.Add(timer);
            return timer;
        }

        public void Advance(TimeSpan by)
        {
            _now += by;
            foreach (var timer in Timers.Where(t => !t.Done && t.Due <= _now)) timer.Fire();
        }
    }

    private sealed class ManualTimer(TimerCallback callback, object? state, DateTimeOffset due) : ITimer
    {
        public DateTimeOffset Due { get; } = due;
        public bool Done { get; private set; }

        public void Fire()
        {
            Done = true;
            callback(state);
        }

        /// <summary>The callback, as a pool thread that picked it up just before the timer was cancelled would run it.</summary>
        public void InvokeAnyway() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException("KeepAwakeService never reschedules");

        public void Dispose() => Done = true;

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "astra-keepawake-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
        public string Script(string name, string body)
        {
            var path = System.IO.Path.Combine(Path, name);
            File.WriteAllText(path, "#!/bin/sh\n" + body);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return path;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}

/// <summary>A fact that runs on macOS only (it touches the real power management).</summary>
public sealed class MacOSFactAttribute : FactAttribute
{
    public MacOSFactAttribute()
    {
        if (!OperatingSystem.IsMacOS()) Skip = "macOS only";
    }
}
