using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace NovaStarProxy.Tests;

/// <summary>
/// Stand-in for the NovaPro UHD Jr's TCP port 5200: one session at a time, and any second concurrent client has its
/// socket closed immediately.
/// </summary>
internal sealed class FakeController : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentQueue<Socket> _sessions = new();
    private readonly SemaphoreSlim _sessionAccepted = new(0);
    private readonly Task _acceptLoop;
    private int _acceptedCount;
    private Socket? _current;

    public FakeController()
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = AcceptLoopAsync();
    }

    public int Port { get; }

    public int AcceptedCount => Volatile.Read(ref _acceptedCount);

    /// <summary>The next client that was given the session.</summary>
    public async Task<Socket> NextSessionAsync()
    {
        if (!await _sessionAccepted.WaitAsync(TimeSpan.FromSeconds(10)))
            throw new TimeoutException("No client connected to the fake controller.");
        _sessions.TryDequeue(out var socket);
        return socket!;
    }

    /// <summary>Frees the session slot once the test has seen the current client disconnect.</summary>
    public void EndSession() => Volatile.Write(ref _current, null);

    private async Task AcceptLoopAsync()
    {
        while (true)
        {
            Socket client;
            try
            {
                client = await _listener.AcceptSocketAsync(_stop.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }
            Interlocked.Increment(ref _acceptedCount);
            if (Volatile.Read(ref _current) is not null)
            {
                client.Dispose();
                continue;
            }
            client.NoDelay = true;
            Volatile.Write(ref _current, client);
            _sessions.Enqueue(client);
            _sessionAccepted.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        await _acceptLoop;
        foreach (var socket in _sessions)
            socket.Dispose();
        _current?.Dispose();
    }
}

internal static class TestNetwork
{
    /// <summary>A connected loopback pair standing in for a com0com pair: (NovaLCT's end, the proxy's end).</summary>
    public static async Task<(Socket App, Socket Proxy)> CreateSerialPairAsync()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var app = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        var accept = listener.AcceptSocketAsync();
        await app.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        var proxy = await accept;
        proxy.NoDelay = true;
        return (app, proxy);
    }

    public static byte[] RandomBytes(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    /// <summary>Writes <paramref name="data"/> in randomly sized pieces to exercise fragmentation.</summary>
    public static async Task SendInChunksAsync(Socket socket, byte[] data, int seed)
    {
        var random = new Random(seed);
        await using var stream = new NetworkStream(socket, ownsSocket: false);
        for (var offset = 0; offset < data.Length;)
        {
            var size = Math.Min(random.Next(1, 8193), data.Length - offset);
            await stream.WriteAsync(data.AsMemory(offset, size));
            offset += size;
        }
    }

    public static async Task<byte[]> ReceiveExactlyAsync(Socket socket, int length)
    {
        var buffer = new byte[length];
        var received = 0;
        while (received < length)
        {
            var n = await socket.ReceiveAsync(buffer.AsMemory(received), SocketFlags.None);
            if (n == 0)
                throw new EndOfStreamException($"Connection closed after {received} of {length} bytes.");
            received += n;
        }
        return buffer;
    }

    /// <summary>Waits for the peer to close; returns false if it hasn't within the timeout.</summary>
    public static async Task<bool> WaitForCloseAsync(Socket socket, int timeoutMs = 5000)
    {
        using var timeout = new CancellationTokenSource(timeoutMs);
        var scratch = new byte[4096];
        try
        {
            while (await socket.ReceiveAsync(scratch, SocketFlags.None, timeout.Token) > 0)
            {
            }
            return true;
        }
        catch (SocketException)
        {
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    public static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 10000)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.ElapsedMilliseconds > timeoutMs)
                throw new TimeoutException("Condition not met in time.");
            await Task.Delay(20);
        }
    }
}
