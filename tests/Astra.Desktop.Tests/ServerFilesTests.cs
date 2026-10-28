using Astra.Desktop.Core;

namespace Astra.Desktop.Tests;

public class ServerFilesTests
{
    [Fact]
    public void Runtime_parses_known_fields_and_ignores_the_rest()
    {
        var r = RuntimeInfo.Parse("""{"pid":42,"port":17400,"host":"127.0.0.1","version":"0.11.7","apiVersion":"1.0","runtimeToken":"t","extra":1}""");
        Assert.Equal(new RuntimeInfo(42, 17400, "127.0.0.1", "0.11.7", "1.0", null, null, "t"), r);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"port":1}""")]
    [InlineData("""{"pid":0}""")]
    [InlineData("""{"pid":1.5}""")]
    [InlineData("""{"pid":"7"}""")]
    public void Runtime_rejects_missing_or_invalid_content(string? text) => Assert.Null(RuntimeInfo.Parse(text));

    [Theory]
    [InlineData("""{"pid":1,"port":"18000"}""", 18000)]
    [InlineData("""{"pid":1,"port":0}""", 17321)]
    [InlineData("""{"pid":1,"port":70000}""", 17321)]
    [InlineData("""{"pid":1}""", 17321)]
    public void Port_is_normalized(string text, int expected) => Assert.Equal(expected, RuntimeInfo.Parse(text)!.Port);

    [Fact]
    public void IsFrom_matches_the_spawned_pid_or_a_server_started_after_the_spawn()
    {
        var spawn = DateTimeOffset.Parse("2026-10-26T10:00:00Z");
        Assert.True(new RuntimeInfo(5, 1).IsFrom(5, spawn));
        Assert.True(new RuntimeInfo(9, 1, StartedAt: "2026-10-26T10:00:01Z").IsFrom(5, spawn));
        Assert.False(new RuntimeInfo(9, 1, StartedAt: "2026-10-26T09:59:59Z").IsFrom(5, spawn));
        Assert.False(new RuntimeInfo(9, 1).IsFrom(5, spawn));
    }

    [Fact]
    public void Config_falls_back_to_defaults()
    {
        Assert.Equal(ServerConfig.Default, ServerConfig.Parse(null));
        Assert.Equal(ServerConfig.Default, ServerConfig.Parse("nope"));
        Assert.Equal(new ServerConfig(18000, "0.0.0.0"), ServerConfig.Parse("""{"port":18000,"host":"0.0.0.0"}"""));
        Assert.Equal(new ServerConfig(17321, "127.0.0.1"), ServerConfig.Parse("""{"port":-1,"host":" "}"""));
    }

    [Fact]
    public void Install_reads_the_server_fields()
    {
        var i = InstallInfo.Parse("""{"serverPath":"/x/astra-server-0.12.0","serverVersion":"0.12.0","desktopPath":"/d"}""");
        Assert.Equal(new InstallInfo("/x/astra-server-0.12.0", "0.12.0"), i);
        Assert.Null(InstallInfo.Parse("[1]"));
    }
}
