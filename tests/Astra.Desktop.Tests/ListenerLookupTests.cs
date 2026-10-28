using Astra.Desktop.Core;

namespace Astra.Desktop.Tests;

public class ListenerLookupTests
{
    [Fact]
    public void Lsof_output_is_one_pid_per_line()
        => Assert.Equal([123, 45], ListenerLookup.ParseLsofPids("123\n 45 \nnot\n0\n"));

    [Fact]
    public void Netstat_listeners_are_recognised_by_their_unconnected_foreign_address()
    {
        const string text = """
              Proto  Local Address          Foreign Address        State           PID
              TCP    0.0.0.0:17321          0.0.0.0:0              LISTENING       4242
              TCP    127.0.0.1:17321        127.0.0.1:50000        ESTABLISHED     999
              TCP    [::]:17321             [::]:0                 ABHÖREN         4242
              TCP    0.0.0.0:80             0.0.0.0:0              LISTENING       7
            """;
        Assert.Equal([4242], ListenerLookup.ParseNetstatListenerPids(text, 17321));
    }

    [Fact]
    public void Tasklist_image_is_the_first_quoted_field()
        => Assert.Equal("astra-server.exe", ListenerLookup.ParseTasklistImage("\"astra-server.exe\",\"4242\",\"Console\""));

    [Theory]
    [InlineData("astra-server", true)]
    [InlineData("/Users/x/.astra/server/astra-server-0.12.0", true)]
    [InlineData("C:\\Astra\\resources\\server\\astra-server.exe", true)]
    [InlineData("ASTRA-SERVER.EXE", true)]
    [InlineData("dotnet", false)]
    [InlineData("astra-serverx", false)]
    [InlineData("my-astra-server", false)]
    public void Server_executable_names(string name, bool expected)
        => Assert.Equal(expected, ListenerLookup.IsAstraServerExecutable(name));

    [Fact]
    public async Task A_listener_that_is_not_astra_server_is_never_returned()
    {
        if (OperatingSystem.IsWindows()) return;
        Task<string?> Run(string cmd, string[] args) => Task.FromResult<string?>(cmd.EndsWith("lsof") ? "77\n" : "nginx\n");
        Assert.Null(await ListenerLookup.FindServerListenerPidAsync(17321, Run));
        Task<string?> Ours(string cmd, string[] args) => Task.FromResult<string?>(cmd.EndsWith("lsof") ? "77\n" : "/x/astra-server\n");
        Assert.Equal(77, await ListenerLookup.FindServerListenerPidAsync(17321, Ours));
    }
}
