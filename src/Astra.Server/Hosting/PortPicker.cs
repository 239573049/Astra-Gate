using System.Net;
using System.Net.Sockets;

namespace Astra.Server.Hosting;

/// <summary>
/// Plan §2: when the configured port is taken at startup, move on to the next free one. The port actually used is
/// what goes into runtime.json; config.json keeps the configured port.
/// </summary>
public static class PortPicker
{
    public const int MaxAttempts = 20;

    /// <summary>The first port in [start, start + attempts) the host can listen on, or null.</summary>
    public static int? FindFree(string host, int start, int attempts = MaxAttempts)
    {
        for (var port = start; port < start + attempts && port <= IPEndPoint.MaxPort; port++)
        {
            if (CanListen(host, port)) return port;
        }
        return null;
    }

    /// <summary>True when every address Kestrel would bind for <paramref name="host"/> is free on that port.</summary>
    public static bool CanListen(string host, int port)
    {
        IPAddress[] addresses;
        if (host is "localhost") addresses = [IPAddress.Loopback, IPAddress.IPv6Loopback];
        else if (host is "0.0.0.0" or "*") addresses = [IPAddress.Any];
        else if (IPAddress.TryParse(host.Trim('[', ']'), out var parsed)) addresses = [parsed];
        else return true; // not an address we can probe; let Kestrel report it

        foreach (var address in addresses)
        {
            var listener = new TcpListener(address, port);
            try
            {
                listener.Start();
            }
            catch (SocketException e) when (e.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied)
            {
                return false;
            }
            catch (SocketException) when (address.Equals(IPAddress.IPv6Loopback))
            {
                // No IPv6 on this machine: Kestrel's ListenLocalhost tolerates that as well.
            }
            finally
            {
                listener.Stop();
            }
        }
        return true;
    }
}
