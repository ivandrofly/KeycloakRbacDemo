using System.Net;
using System.Net.Sockets;

namespace Rbac.Cli;

/// <summary>
/// HTTP handler that connects over IPv4 only. Docker Desktop publishes ports on both
/// 127.0.0.1 and [::1], but on some hosts the IPv6 loopback forward silently hangs.
/// .NET may try [::1] first for "localhost" and then wait on a dead connection, so we
/// resolve to IPv4 addresses only while keeping "localhost" in the URL (token issuer stays stable).
/// Same workaround as in Rbac.Api and Rbac.Wpf.
/// </summary>
public static class Ipv4Http
{
    public static SocketsHttpHandler CreateHandler() => new()
    {
        ConnectCallback = async (ctx, ct) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host, AddressFamily.InterNetwork, ct);
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(addresses, ctx.DnsEndPoint.Port, ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
    };
}
