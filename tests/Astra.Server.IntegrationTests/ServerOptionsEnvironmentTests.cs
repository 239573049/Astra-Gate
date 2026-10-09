using Astra.Server.Hosting;
using Astra.Server.Security;

namespace Astra.Server.IntegrationTests;

public class ServerOptionsEnvironmentTests
{
    private static ServerOptions Apply(Dictionary<string, string> env, ServerOptions? options = null)
    {
        options ??= new ServerOptions();
        options.ApplyEnvironment(name => env.GetValueOrDefault(name));
        return options;
    }

    [Fact]
    public void Empty_Environment_Changes_Nothing()
    {
        var o = Apply([]);
        Assert.Equal("127.0.0.1", o.Host);
        Assert.Equal(ServerOptions.DefaultPort, o.Port);
        Assert.Null(o.PublicUrl);
        Assert.False(o.StrictPort);
        Assert.Null(o.AdminPasswordHash);
    }

    [Fact]
    public void Environment_Overrides_The_Loaded_Config()
    {
        var o = Apply(
            new() { ["ASTRA_HOST"] = "0.0.0.0", ["ASTRA_PORT"] = "8080", ["ASTRA_STRICT_PORT"] = "true" },
            new ServerOptions { Host = "127.0.0.1", Port = 9999 });
        Assert.Equal("0.0.0.0", o.Host);
        Assert.Equal(8080, o.Port);
        Assert.True(o.StrictPort);
    }

    [Fact]
    public void Flags_Win_Over_The_Environment()
    {
        var o = Apply(new() { ["ASTRA_PORT"] = "8080" });
        o.ApplyArgs(["--port", "9090"]);
        Assert.Equal(9090, o.Port);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("70000")]
    [InlineData("abc")]
    public void A_Bad_Port_Is_Rejected(string port) =>
        Assert.Throws<ArgumentException>(() => Apply(new() { ["ASTRA_PORT"] = port }));

    [Fact]
    public void The_Password_Is_Hashed_In_Memory()
    {
        var o = Apply(new() { ["ASTRA_ADMIN_PASSWORD"] = "correct horse" });
        Assert.NotNull(o.AdminPasswordHash);
        Assert.DoesNotContain("correct horse", o.AdminPasswordHash);
        Assert.True(PasswordHasher.Verify("correct horse", o.AdminPasswordHash));
    }

    [Fact]
    public void The_Password_Can_Come_From_A_File_Without_Its_Trailing_Newline()
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, "correct horse\n");
            var o = Apply(new() { ["ASTRA_ADMIN_PASSWORD_FILE"] = file });
            Assert.True(PasswordHasher.Verify("correct horse", o.AdminPasswordHash));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void A_Short_Or_Unreadable_Password_Is_Rejected()
    {
        Assert.Throws<ArgumentException>(() => Apply(new() { ["ASTRA_ADMIN_PASSWORD"] = "short" }));
        Assert.Throws<ArgumentException>(() => Apply(new() { ["ASTRA_ADMIN_PASSWORD_FILE"] = "/nonexistent/astra-password" }));
    }

    [Fact]
    public void The_Public_Url_Replaces_The_Gateway_Base_Url_But_Not_The_Local_One()
    {
        var o = Apply(new() { ["ASTRA_HOST"] = "0.0.0.0", ["ASTRA_PUBLIC_URL"] = "https://gate.example.com/" });
        Assert.Equal("https://gate.example.com", o.GatewayBaseUrl);
        Assert.Equal("http://127.0.0.1:17321", o.LocalUrl);
    }

    [Fact]
    public void Without_A_Public_Url_The_Wildcard_Host_Maps_To_Loopback()
    {
        var o = Apply(new() { ["ASTRA_HOST"] = "0.0.0.0" });
        Assert.Equal("http://127.0.0.1:17321", o.GatewayBaseUrl);
    }

    [Theory]
    [InlineData("gate.example.com")]
    [InlineData("ftp://gate.example.com")]
    public void A_Non_Http_Public_Url_Is_Rejected(string url) =>
        Assert.Throws<ArgumentException>(() => Apply(new() { ["ASTRA_PUBLIC_URL"] = url }));
}
