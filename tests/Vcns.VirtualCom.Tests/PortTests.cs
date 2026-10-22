// SPDX-License-Identifier: MS-PL
// Copyright (c) 2026 VCNS Tech Ltd
using System.Diagnostics;
using System.IO.Ports;

namespace Vcns.VirtualCom.Tests;

/// <summary>Runs only when VCOM_TEST_PORT names an installed virtual port, e.g. COM20.</summary>
public sealed class PortFactAttribute : FactAttribute
{
    public PortFactAttribute()
    {
        if (string.IsNullOrEmpty(PortTests.PortName))
            Skip = "Set VCOM_TEST_PORT to an installed virtual COM port (e.g. COM20).";
    }
}

/// <summary>
/// Exercises a real installed port: the COM end through System.IO.Ports (as typical Windows serial applications
/// use it) and the host end through VirtualComHost.
/// </summary>
[Collection("port")] // one port, so tests must not run in parallel
public sealed class PortTests
{
    public static string? PortName => Environment.GetEnvironmentVariable("VCOM_TEST_PORT");

    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    private static SerialPort OpenCom()
    {
        var port = new SerialPort(PortName!, 115200, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.None,
            DtrEnable = true,
            RtsEnable = true,
            ReadTimeout = 2000,
            WriteTimeout = 2000,
        };
        port.Open();
        return port;
    }

    private static async Task<byte[]> ReadExactlyAsync(Stream stream, int length)
    {
        var buffer = new byte[length];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await stream.ReadExactlyAsync(buffer, timeout.Token);
        return buffer;
    }

    private static async Task WriteInChunksAsync(Stream stream, byte[] data, int seed)
    {
        var random = new Random(seed);
        for (var offset = 0; offset < data.Length;)
        {
            var size = Math.Min(random.Next(1, 4097), data.Length - offset);
            await stream.WriteAsync(data.AsMemory(offset, size));
            offset += size;
        }
    }

    private static void WaitUntil(Func<bool> condition, string what)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > Wait)
                throw new TimeoutException($"Timed out waiting for {what}.");
            Thread.Sleep(10);
        }
    }

    [PortFact]
    public void Port_is_listed_by_serial_port_enumeration() => Assert.Contains(PortName, SerialPort.GetPortNames());

    [PortFact]
    public void Host_sees_the_settings_the_application_chose()
    {
        using var com = OpenCom();
        using var host = VirtualComHost.Open(PortName!);

        var state = host.GetState();

        Assert.Equal(PortName, state.PortName);
        Assert.True(state.ComOpen);
        Assert.True(state.Dtr);
        Assert.True(state.Rts);
        Assert.Equal(115200u, state.BaudRate);
        Assert.Equal(8, state.DataBits);
        Assert.Equal(0, state.Parity);
        Assert.Equal(0, state.StopBits);
    }

    [PortFact]
    public void Each_end_can_be_opened_only_once()
    {
        using var com = OpenCom();
        using var host = VirtualComHost.Open(PortName!);

        Assert.Throws<UnauthorizedAccessException>(() => new SerialPort(PortName!).Open());
        Assert.Throws<IOException>(() => VirtualComHost.Open(PortName!));
    }

    [PortFact]
    public async Task Bytes_flow_both_ways_unchanged()
    {
        using var com = OpenCom();
        using var host = VirtualComHost.Open(PortName!);
        var toHost = new byte[256 * 1024];
        var toCom = new byte[256 * 1024];
        new Random(1).NextBytes(toHost);
        new Random(2).NextBytes(toCom);

        var receivedByHost = ReadExactlyAsync(host.Stream, toHost.Length);
        var receivedByCom = ReadExactlyAsync(com.BaseStream, toCom.Length);
        await Task.WhenAll(WriteInChunksAsync(com.BaseStream, toHost, 3), WriteInChunksAsync(host.Stream, toCom, 4));

        Assert.Equal(toHost, await receivedByHost);
        Assert.Equal(toCom, await receivedByCom);
    }

    [PortFact]
    public async Task Bursts_larger_than_the_driver_buffer_arrive_intact()
    {
        using var com = OpenCom();
        using var host = VirtualComHost.Open(PortName!);
        var data = new byte[600 * 1024];   // several times the 64 KB buffer: the host's writes must wait for space
        new Random(5).NextBytes(data);

        var write = host.Stream.WriteAsync(data).AsTask();
        await Task.Delay(500);              // let the buffer fill before the application starts reading
        Assert.Equal(data, await ReadExactlyAsync(com.BaseStream, data.Length));
        await write;
    }

    [PortFact]
    public void Read_times_out_when_no_data_arrives()
    {
        using var com = OpenCom();
        using var host = VirtualComHost.Open(PortName!);
        com.ReadTimeout = 300;

        var watch = Stopwatch.StartNew();
        Assert.Throws<TimeoutException>(() => com.ReadByte());

        Assert.InRange(watch.ElapsedMilliseconds, 250, 1500);
    }

    [PortFact]
    public async Task Read_returns_as_soon_as_data_arrives()
    {
        using var com = OpenCom();
        using var host = VirtualComHost.Open(PortName!);
        com.ReadTimeout = SerialPort.InfiniteTimeout;
        var buffer = new byte[100];

        var watch = Stopwatch.StartNew();
        var read = Task.Run(() => com.Read(buffer, 0, buffer.Length));
        await Task.Delay(200);
        await host.Stream.WriteAsync(new byte[] { 1, 2, 3 });

        Assert.Equal(3, await read.WaitAsync(Wait));
        Assert.InRange(watch.ElapsedMilliseconds, 150, 2000);
    }

    [PortFact]
    public void Modem_lines_follow_the_host_end()
    {
        using var com = OpenCom();
        Assert.False(com.CtsHolding || com.DsrHolding || com.CDHolding, "No host attached: lines should be low.");
        var pinChanges = 0;
        com.PinChanged += (_, _) => Interlocked.Increment(ref pinChanges);

        using (var host = VirtualComHost.Open(PortName!))
        {
            WaitUntil(() => com.CtsHolding && com.DsrHolding && com.CDHolding, "CTS, DSR and DCD to rise");
            WaitUntil(() => Volatile.Read(ref pinChanges) > 0, "a PinChanged event");

            host.SetModemLines(ModemLines.Dsr);
            WaitUntil(() => !com.CtsHolding && com.DsrHolding && !com.CDHolding, "only DSR to stay high");
        }

        WaitUntil(() => !com.CtsHolding && !com.DsrHolding && !com.CDHolding, "all lines to drop when the host closes");
    }

    [PortFact]
    public void Buffered_bytes_are_counted_and_can_be_discarded()
    {
        using var com = OpenCom();
        using var host = VirtualComHost.Open(PortName!);

        host.Stream.Write(new byte[10]);
        WaitUntil(() => com.BytesToRead == 10, "10 bytes waiting");
        com.DiscardInBuffer();

        Assert.Equal(0, com.BytesToRead);
    }

    [PortFact]
    public void Data_received_event_fires()
    {
        using var com = OpenCom();
        using var host = VirtualComHost.Open(PortName!);
        using var received = new ManualResetEventSlim();
        com.DataReceived += (_, _) => received.Set();

        host.Stream.Write("hello"u8);

        Assert.True(received.Wait(Wait), "DataReceived was not raised.");
        Assert.Equal("hello", com.ReadExisting());
    }

    [PortFact]
    public async Task Writes_with_no_host_attached_are_discarded()
    {
        using var com = OpenCom();
        com.Write(new byte[100], 0, 100);     // must not block

        using var host = VirtualComHost.Open(PortName!);
        using var nothing = new CancellationTokenSource(300);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.Stream.ReadAsync(new byte[1], nothing.Token).AsTask());
    }

    [PortFact]
    public async Task Host_read_waits_until_the_application_writes()
    {
        using var com = OpenCom();
        using var host = VirtualComHost.Open(PortName!);
        var buffer = new byte[16];

        var read = host.Stream.ReadAsync(buffer).AsTask();
        await Task.Delay(200);
        Assert.False(read.IsCompleted);
        com.Write(new byte[] { 42 }, 0, 1);

        Assert.Equal(1, await read.WaitAsync(Wait));
        Assert.Equal(42, buffer[0]);
    }

    [PortFact]
    public async Task Closing_the_application_end_fails_its_pending_read()
    {
        var com = OpenCom();
        com.ReadTimeout = SerialPort.InfiniteTimeout;
        var read = Task.Run(() => com.BaseStream.Read(new byte[10]));
        await Task.Delay(200);

        com.Dispose();

        await Assert.ThrowsAnyAsync<Exception>(() => read.WaitAsync(Wait));
    }
}
