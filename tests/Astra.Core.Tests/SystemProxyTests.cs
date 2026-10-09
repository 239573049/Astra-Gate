namespace Astra.Core.Tests;

/// <summary>Env-var mutation is process-wide: one class, one collection, restored in a finally.</summary>
[Collection("ProxyEnvironment")]
public class SystemProxyTests
{
    private static readonly string[] Names = ["HTTPS_PROXY", "https_proxy", "HTTP_PROXY", "http_proxy", "ALL_PROXY", "all_proxy", "NO_PROXY", "no_proxy"];

    private static void WithEnv(IReadOnlyDictionary<string, string> env, Action body)
    {
        var saved = Names.ToDictionary(n => n, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var n in Names) Environment.SetEnvironmentVariable(n, null);
            foreach (var (k, v) in env) Environment.SetEnvironmentVariable(k, v);
            body();
        }
        finally
        {
            foreach (var (k, v) in saved) Environment.SetEnvironmentVariable(k, v);
        }
    }

    [Fact]
    public void Dynamic_Uses_The_Environment_Proxy_For_External_Hosts()
    {
        WithEnv(new Dictionary<string, string> { ["HTTPS_PROXY"] = "http://127.0.0.1:10808" }, () =>
        {
            var proxy = SystemProxy.Dynamic.GetProxy(new Uri("https://api.anthropic.com/v1/messages"));
            Assert.Equal(new Uri("http://127.0.0.1:10808"), proxy);
        });
    }

    [Fact]
    public void Dynamic_Keeps_Loopback_And_NoProxy_Hosts_Direct()
    {
        WithEnv(new Dictionary<string, string> { ["HTTPS_PROXY"] = "http://127.0.0.1:10808", ["NO_PROXY"] = "internal.example.com" }, () =>
        {
            Assert.True(SystemProxy.Dynamic.IsBypassed(new Uri("http://127.0.0.1:11434/v1")));
            Assert.True(SystemProxy.Dynamic.IsBypassed(new Uri("http://localhost:1234/v1")));
            Assert.True(SystemProxy.Dynamic.IsBypassed(new Uri("https://internal.example.com/v1")));
            Assert.False(SystemProxy.Dynamic.IsBypassed(new Uri("https://api.openai.com/v1")));
        });
    }

    [Fact]
    public void Dynamic_Follows_Environment_Changes_After_Creation()
    {
        var target = new Uri("https://api.anthropic.com/");
        WithEnv(new Dictionary<string, string> { ["HTTPS_PROXY"] = "http://127.0.0.1:1111" }, () =>
            Assert.Equal(new Uri("http://127.0.0.1:1111"), SystemProxy.Dynamic.GetProxy(target)));
        WithEnv(new Dictionary<string, string> { ["HTTPS_PROXY"] = "http://127.0.0.1:2222" }, () =>
            Assert.Equal(new Uri("http://127.0.0.1:2222"), SystemProxy.Dynamic.GetProxy(target)));
    }

    /// <summary>The settings override is process-wide too: always reset to "system" afterwards.</summary>
    private static void WithSettings(string mode, string? url, string? bypass, System.Net.NetworkCredential? credentials, Action body)
    {
        try
        {
            SystemProxy.Configure(mode, url, bypass, credentials);
            body();
        }
        finally
        {
            SystemProxy.Configure(SystemProxy.ModeSystem, null, null, null);
        }
    }

    [Fact]
    public void Custom_Mode_Overrides_The_Environment_And_Honours_Its_Bypass_List()
    {
        WithEnv(new Dictionary<string, string> { ["HTTPS_PROXY"] = "http://127.0.0.1:1111" }, () =>
            WithSettings(SystemProxy.ModeCustom, "socks5://10.0.0.2:1080", "corp.example.com, *.lan", null, () =>
            {
                Assert.Equal(SystemProxy.ModeCustom, SystemProxy.Mode);
                Assert.Equal(new Uri("socks5://10.0.0.2:1080"), SystemProxy.Dynamic.GetProxy(new Uri("https://api.openai.com/v1")));
                Assert.True(SystemProxy.Dynamic.IsBypassed(new Uri("https://git.corp.example.com/")));
                Assert.True(SystemProxy.Dynamic.IsBypassed(new Uri("http://nas.lan:8080/")));
                Assert.True(SystemProxy.Dynamic.IsBypassed(new Uri("http://127.0.0.1:11434/v1")));
            }));
    }

    [Fact]
    public void Direct_Mode_Ignores_The_Environment_Proxy()
    {
        WithEnv(new Dictionary<string, string> { ["HTTPS_PROXY"] = "http://127.0.0.1:1111" }, () =>
            WithSettings(SystemProxy.ModeDirect, null, null, null, () =>
            {
                Assert.True(SystemProxy.Dynamic.IsBypassed(new Uri("https://api.anthropic.com/")));
                Assert.Null(SystemProxy.Detect());
            }));
    }

    [Fact]
    public void Custom_Credentials_Reach_The_Handler_And_Child_Processes()
    {
        var target = new Uri("https://api.anthropic.com/");
        WithSettings(SystemProxy.ModeCustom, "http://proxy.example.com:3128", null, new System.Net.NetworkCredential("me", "p@ss:word"), () =>
        {
            var credential = SystemProxy.Dynamic.Credentials!.GetCredential(new Uri("http://proxy.example.com:3128"), "Basic");
            Assert.Equal("me", credential!.UserName);
            Assert.Equal("p@ss:word", credential.Password);
            Assert.Equal(("http://me:p%40ss%3Aword@proxy.example.com:3128", (string?)null), SystemProxy.CustomForChildren);
            Assert.Equal(new Uri("http://proxy.example.com:3128"), SystemProxy.Dynamic.GetProxy(target));
        });
        Assert.Null(SystemProxy.CustomForChildren);
    }

    [Fact]
    public void Environment_Proxy_Credentials_Are_Split_Out_Of_The_Url()
    {
        WithEnv(new Dictionary<string, string> { ["HTTPS_PROXY"] = "http://user:secret@127.0.0.1:10808" }, () =>
        {
            Assert.Equal(new Uri("http://127.0.0.1:10808"), SystemProxy.Dynamic.GetProxy(new Uri("https://api.openai.com/")));
            var credential = SystemProxy.Dynamic.Credentials!.GetCredential(new Uri("http://127.0.0.1:10808"), "Basic");
            Assert.Equal("secret", credential!.Password);
        });
    }

    [Fact]
    public async Task Handler_Answers_A_Proxy_Auth_Challenge_With_Credentials_Set_After_It_Was_Built()
    {
        // A one-connection HTTP proxy: 407 until the request carries Basic me:pw, then 200.
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        var seen = new List<string>();
        var server = Task.Run(async () =>
        {
            for (var i = 0; i < 2; i++)
            {
                using var socket = await listener.AcceptTcpClientAsync();
                var stream = socket.GetStream();
                var reader = new StreamReader(stream);
                var head = new System.Text.StringBuilder();
                while (await reader.ReadLineAsync() is { Length: > 0 } line) head.AppendLine(line);
                seen.Add(head.ToString());
                var authorized = head.ToString().Contains("Proxy-Authorization: Basic " + Convert.ToBase64String("me:pw"u8.ToArray()));
                var response = authorized
                    ? "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"
                    : "HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic realm=\"t\"\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(response));
                if (authorized) return;
            }
        });

        using var http = new HttpClient(SystemProxy.CreateHandler(TimeSpan.FromSeconds(5)));
        await WithSettingsAsync(SystemProxy.ModeCustom, $"http://127.0.0.1:{port}", null, new System.Net.NetworkCredential("me", "pw"), async () =>
        {
            using var res = await http.GetAsync("http://upstream.example.invalid/v1");
            Assert.Equal(System.Net.HttpStatusCode.OK, res.StatusCode);
        });
        await server;
        listener.Stop();
        Assert.StartsWith("GET http://upstream.example.invalid/v1", seen[^1]);
    }

    private static async Task WithSettingsAsync(string mode, string? url, string? bypass, System.Net.NetworkCredential? credentials, Func<Task> body)
    {
        try
        {
            SystemProxy.Configure(mode, url, bypass, credentials);
            await body();
        }
        finally
        {
            SystemProxy.Configure(SystemProxy.ModeSystem, null, null, null);
        }
    }
}
