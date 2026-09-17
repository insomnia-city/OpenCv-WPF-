using System.Net;
using System.Net.Sockets;

namespace HalconWorkflow.Protocols.Modbus;

/// <summary>
/// In-process Modbus TCP slave on loopback (no hardware): accepts connections and
/// serves coils / discrete inputs / holding / input registers over real MBAP frames.
/// Used by tests and by the App's demo loopback device.  · / 进程内 Modbus TCP 从站(回环，
///   无需硬件)：按真实 MBAP 帧服务线圈/离散输入/保持/输入寄存器。供测试与 App 演示用。
/// </summary>
public sealed class ModbusTcpSimulator : IAsyncDisposable
{
    public const int DefaultMapSize = 512;

    private readonly TcpListener _listener;
    private readonly object _gate = new();
    private readonly bool[] _coils;
    private readonly bool[] _discretes;
    private readonly ushort[] _holding;
    private readonly ushort[] _inputs;
    private readonly List<Task> _clients = [];
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    private ModbusTcpSimulator(TcpListener listener, int mapSize)
    {
        _listener = listener;
        _coils = new bool[mapSize];
        _discretes = new bool[mapSize];
        _holding = new ushort[mapSize];
        _inputs = new ushort[mapSize];
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>Started port on loopback. / 回环上监听的端口</summary>
    public int Port { get; }

    /// <summary>Starts a simulator on an ephemeral loopback port. / 在临时回环端口启动模拟器</summary>
    public static ModbusTcpSimulator Start(int mapSize = DefaultMapSize)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var sim = new ModbusTcpSimulator(listener, mapSize);
        _ = sim.AcceptLoopAsync();
        return sim;
    }

    // ----- direct map access for seeding / asserting · 直接读写内存，用于播种与断言 -----

    public void SeedCoil(int index, bool value)
    {
        lock (_gate) _coils[index] = value;
    }

    public void SeedHolding(int index, ushort value)
    {
        lock (_gate) _holding[index] = value;
    }

    public bool ReadCoil(int index)
    {
        lock (_gate) return _coils[index];
    }

    public ushort ReadHolding(int index)
    {
        lock (_gate) return _holding[index];
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _listener.Stop();
        try
        {
            using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await Task.WhenAny(Task.WhenAll(_clients.ToArray()), Task.Delay(Timeout.Infinite, guard.Token))
                      .ConfigureAwait(false);
        }
        catch
        {
            // sockets torn down by Stop · 由 Stop 拆掉的套接字
        }
        _cts.Dispose();
        _clients.Clear();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
            }
            catch (SocketException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            _clients.Add(ServeClientAsync(client));
        }
    }

    private async Task ServeClientAsync(TcpClient client)
    {
        try
        {
            using var stream = client.GetStream();
            var header = new byte[7];
            var pdu = new byte[255];
            while (!_cts.IsCancellationRequested)
            {
                if (!await TryReadExactlyAsync(stream, header).ConfigureAwait(false)) return;
                var tid = (byte)0;
                _ = tid;
                var transactionId = (ushort)((header[0] << 8) | header[1]);
                var length = (ushort)((header[4] << 8) | header[5]);
                var unitId = header[6];
                if (length < 1 || length - 1 > pdu.Length) return;
                if (!await TryReadExactlyAsync(stream, pdu.AsMemory(0, length - 1)).ConfigureAwait(false)) return;
                var resp = Execute(transactionId, unitId, pdu.AsSpan(0, length - 1).ToArray());
                await stream.WriteAsync(resp, _cts.Token).ConfigureAwait(false);
                await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // client disconnect or disposed server; finish the task · 客户端断开或服务器释放
        }
        finally
        {
            client.Dispose();
        }
    }

    private byte[] Execute(ushort transactionId, byte unitId, byte[] pdu)
    {
        if (pdu.Length == 0) return ExceptionFrame(transactionId, unitId, 0x00, 0x01);
        var fc = pdu[0];
        switch (fc)
        {
            case 0x01: return ReadBits(transactionId, unitId, fc, _coils, pdu);
            case 0x02: return ReadBits(transactionId, unitId, fc, _discretes, pdu);
            case 0x03: return ReadWords(transactionId, unitId, fc, _holding, pdu);
            case 0x04: return ReadWords(transactionId, unitId, fc, _inputs, pdu);
            case 0x05: return WriteSingleBit(transactionId, unitId, fc, _coils, pdu);
            case 0x06: return WriteSingleWord(transactionId, unitId, fc, _holding, pdu);
            default: return ExceptionFrame(transactionId, unitId, fc, 0x01);
        }
    }

    private byte[] ReadBits(ushort tid, byte unit, byte fc, bool[] map, byte[] pdu)
    {
        if (pdu.Length < 5) return ExceptionFrame(tid, unit, fc, 0x03);
        int start = (pdu[1] << 8) | pdu[2];
        int qty = (pdu[3] << 8) | pdu[4];
        lock (_gate)
        {
            if (qty is < 1 or > 4000 || start + qty > map.Length)
                return ExceptionFrame(tid, unit, fc, 0x02);
            int byteCount = (qty + 7) / 8;
            var data = new byte[byteCount];
            for (int i = 0; i < qty; i++)
            {
                if (map[start + i]) data[i / 8] |= (byte)(1 << (i % 8));
            }
            return Response(tid, unit, fc, [.. new[] { (byte)byteCount }, .. data]);
        }
    }

    private byte[] ReadWords(ushort tid, byte unit, byte fc, ushort[] map, byte[] pdu)
    {
        if (pdu.Length < 5) return ExceptionFrame(tid, unit, fc, 0x03);
        int start = (pdu[1] << 8) | pdu[2];
        int qty = (pdu[3] << 8) | pdu[4];
        lock (_gate)
        {
            if (qty is < 1 or > 64 || start + qty > map.Length)
                return ExceptionFrame(tid, unit, fc, 0x02);
            var data = new byte[qty * 2];
            for (int i = 0; i < qty; i++)
            {
                ushort v = map[start + i];
                data[i * 2] = (byte)(v >> 8);
                data[i * 2 + 1] = (byte)v;
            }
            return Response(tid, unit, fc, [.. new[] { (byte)(qty * 2) }, .. data]);
        }
    }

    private byte[] WriteSingleBit(ushort tid, byte unit, byte fc, bool[] map, byte[] pdu)
    {
        if (pdu.Length < 5) return ExceptionFrame(tid, unit, fc, 0x03);
        int start = (pdu[1] << 8) | pdu[2];
        bool value = pdu[3] == 0xFF;
        lock (_gate)
        {
            if (start >= map.Length) return ExceptionFrame(tid, unit, fc, 0x02);
            map[start] = value;
            return Response(tid, unit, fc, pdu[1..]);
        }
    }

    private byte[] WriteSingleWord(ushort tid, byte unit, byte fc, ushort[] map, byte[] pdu)
    {
        if (pdu.Length < 5) return ExceptionFrame(tid, unit, fc, 0x03);
        int start = (pdu[1] << 8) | pdu[2];
        ushort value = (ushort)((pdu[3] << 8) | pdu[4]);
        lock (_gate)
        {
            if (start >= map.Length) return ExceptionFrame(tid, unit, fc, 0x02);
            map[start] = value;
            return Response(tid, unit, fc, pdu[1..]);
        }
    }

    private static byte[] Response(ushort tid, byte unit, byte fc, byte[] payload)
    {
        // Length = UnitId (1) + FC (1) + payload bytes. Total ADU = 6 + length.
        // 长度 = UnitId(1)+FC(1)+payload 字节，总帧长 = 6+length。
        var length = 2 + payload.Length;
        var frame = new byte[6 + length];
        frame[0] = (byte)(tid >> 8);
        frame[1] = (byte)tid;
        frame[2] = 0;
        frame[3] = 0;
        frame[4] = (byte)(length >> 8);
        frame[5] = (byte)length;
        frame[6] = unit;
        frame[7] = fc;
        payload.CopyTo(frame, 8);
        return frame;
    }

    private static byte[] ExceptionFrame(ushort tid, byte unit, byte fc, byte code)
        => Response(tid, unit, (byte)(fc | 0x80), [code]);

    private static async Task<bool> TryReadExactlyAsync(Stream s, Memory<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = await s.ReadAsync(buffer[total..]).ConfigureAwait(false);
            if (n == 0) return false;
            total += n;
        }
        return true;
    }
}