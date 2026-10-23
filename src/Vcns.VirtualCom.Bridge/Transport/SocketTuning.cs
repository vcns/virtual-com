using System.Net.Sockets;
using Vcns.VirtualCom.Bridge.Diagnostics;

namespace Vcns.VirtualCom.Bridge.Transport;

/// <summary>TCP behaviour shared by client sessions and shared (server-mode) ports.</summary>
internal static class SocketTuning
{
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Detects a dead link within about 30 s even when no data is flowing, so the state shown is never stale for long.
    /// </summary>
    public static void ApplyKeepAlive(Socket socket, string logContext)
    {
        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 10);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 2);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 5);
            // TCP_MAXRT: give up on unacknowledged data after 30 s rather than Windows' default of a minute or more.
            socket.SetSocketOption(SocketOptionLevel.Tcp, (SocketOptionName)5, 30);
        }
        catch (SocketException ex)
        {
            Log.Warn($"{logContext}: could not configure TCP keep-alive ({ex.Message}).");
        }
    }

    public static async Task CloseGracefullyAsync(Socket socket)
    {
        try
        {
            // Send FIN and read whatever is still in flight, so Windows doesn't answer unread data with a reset and the
            // peer sees an orderly close it can release promptly.
            socket.Shutdown(SocketShutdown.Send);
            var scratch = new byte[4096];
            using var drain = new CancellationTokenSource(DrainTimeout);
            while (await socket.ReceiveAsync(scratch, SocketFlags.None, drain.Token) > 0)
            {
            }
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
        finally
        {
            socket.Dispose();
        }
    }
}
