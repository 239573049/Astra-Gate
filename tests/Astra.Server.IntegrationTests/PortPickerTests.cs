using System.Net;
using System.Net.Sockets;
using Astra.Server.Hosting;

namespace Astra.Server.IntegrationTests;

public class PortPickerTests
{
    [Fact]
    public void A_Busy_Port_Moves_To_The_Next_Free_One()
    {
        var busy = new TcpListener(IPAddress.Loopback, 0);
        busy.Start();
        try
        {
            var port = ((IPEndPoint)busy.LocalEndpoint).Port;
            Assert.False(PortPicker.CanListen("127.0.0.1", port));
            var picked = PortPicker.FindFree("127.0.0.1", port);
            Assert.NotNull(picked);
            Assert.InRange(picked.Value, port + 1, port + PortPicker.MaxAttempts - 1);
            Assert.True(PortPicker.CanListen("127.0.0.1", picked.Value));
        }
        finally
        {
            busy.Stop();
        }
    }

    [Fact]
    public void A_Free_Port_Is_Kept_And_Probing_Does_Not_Hold_It()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        Assert.Equal(port, PortPicker.FindFree("127.0.0.1", port));
        // The probe released it: something else can bind right after.
        var again = new TcpListener(IPAddress.Loopback, port);
        again.Start();
        again.Stop();
    }

    [Fact]
    public void Gives_Up_When_The_Whole_Range_Is_Busy()
    {
        var busy = new TcpListener(IPAddress.Loopback, 0);
        busy.Start();
        try
        {
            var port = ((IPEndPoint)busy.LocalEndpoint).Port;
            Assert.Null(PortPicker.FindFree("127.0.0.1", port, attempts: 1));
        }
        finally
        {
            busy.Stop();
        }
    }
}
