using System.Net;
using System.Net.Sockets;

namespace HalconWorkflow.Protocols.S7;

/// <summary>
/// In-process S7 ISO-on-TCP/TPKT loopback slave (no hardware), the twin of
/// ModbusTcpSimulator: tests and the App's demo device talk to it over real
/// TPKT/COTP + S7 PDU frames. Liveness mirrors a real S7 (answers, no frame
/// error). Used by no-hardware-green tests (stage-22 P0, ADR-026).
/// 阶段22 P0 的 S7 进程内回环从站(无硬件)。注释见底层 ASCII 版。
/// </summary>
public sealed class S7TcpSimulator : IAsyncDisposable
{
    public const int MaxDbBytes = 256;

    private readonly TcpListener _listener;
    private readonly byte[] _db = new byte[MaxDbBytes];
    private readonly CancellationTokenSource _cts = new();
    private readonly List<TcpClient> _clients = [];
    private readonly object _gate = new();
    private bool _disposed;
    private int _pduRef;

    private S7TcpSimulator(TcpListener listener)
    {
        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>Ephemeral loopback port. 临时回环端口</summary>
    public int Port { get; }

    /// <summary>Starts the simulator on an ephemeral loopback port. 在临时回环端口启动模拟器</summary>
    public static S7TcpSimulator Start()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var sim = new S7TcpSimulator(listener);
        _ = sim.AcceptLoopAsync();
        return sim;
    }

    /// <summary>Seeds a DB byte for tests. 测试注入 DB 字节</summary>
    public void SeedDb(int byteOffset, byte value)
    {
        lock (_gate)
        {
            if ((uint)byteOffset < _db.Length) _db[byteOffset] = value;
        }
    }

    /// <summary>Reads a DB byte for assertions. 断言读取 DB 字节</summary>
    public byte ReadDb(int byteOffset)
    {
        lock (_gate) return (uint)byteOffset < _db.Length ? _db[byteOffset] : (byte)0;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _listener.Stop();
        foreach (var c in _clients) c.Dispose();
        _clients.Clear();
        _cts.Dispose();
        await Task.CompletedTask.ConfigureAwait(false);
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
            catch (SocketException) { return; }
            catch (ObjectDisposedException) { return; }
            lock (_gate) _clients.Add(client);
            _ = ServeClientAsync(client);
        }
    }

    private async Task ServeClientAsync(TcpClient client)
    {
        try
        {
                    using var stream = client.GetStream();
        var head = new byte[4];
        while (!_cts.IsCancellationRequested)
        {
            if (!await TryReadExactlyAsync(stream, head).ConfigureAwait(false)) return;
            if (head[0] != 0x03) return;
            int total = (head[2] << 8) | head[3];
            if (total < 4) return;
            var body = new byte[total - 4];
            if (!await TryReadExactlyAsync(stream, body).ConfigureAwait(false)) return;
                        await HandleBodyAsync(stream, body).ConfigureAwait(false);

        }
        }
        catch (Exception) { }
        finally
        {
            lock (_gate) _clients.Remove(client);
            client.Dispose();
        }
    }

    private static async Task<bool> TryReadExactlyAsync(Stream s, byte[] buf)
    {
        int got = 0;
        while (got < buf.Length)
        {
            int r = await s.ReadAsync(buf.AsMemory(got)).ConfigureAwait(false);
            if (r <= 0) return false;
            got += r;
        }
        return true;
    }

    private async Task HandleBodyAsync(Stream stream, byte[] body)
    {
        if (body.Length >= 2 && body[1] == 0xE0)
        {
            await WriteConnectConfirmAsync(stream).ConfigureAwait(false);
            return;
        }
        if (body.Length >= 12 && body[1] == 0xF0)
        {
            int li = body[0];
            var pdu = new byte[body.Length - li - 1];
            Array.Copy(body, li + 1, pdu, 0, pdu.Length);
            await HandlePduAsync(stream, pdu).ConfigureAwait(false);
        }
    }

    private async Task HandlePduAsync(Stream stream, byte[] pdu)
    {
        if (pdu.Length < 10 || pdu[0] != 0x01) return;
        ushort pduRef = (ushort)((pdu[5] << 8) | pdu[6]);
        ushort paramLen = (ushort)((pdu[7] << 8) | pdu[8]);
        if (paramLen < 2 || pdu.Length < 10 + paramLen) return;
        byte fn = pdu[10];
        if (fn == 0x04)
            await ReplyReadAsync(stream, pduRef, pdu, paramLen).ConfigureAwait(false);
        else if (fn == 0x05)
            await ReplyWriteAsync(stream, pduRef, pdu, paramLen).ConfigureAwait(false);
    }

    private async Task ReplyReadAsync(Stream stream, ushort pduRef, byte[] pdu, int paramLen)
    {
        if (pdu.Length < 10 + paramLen + 14) return;
        int i = 10 + paramLen;
        int db = (pdu[i + 8] << 8) | pdu[i + 9];
        int off = (pdu[i + 11] << 8) | pdu[i + 12];
        int len = (pdu[i + 4] << 8) | pdu[i + 5];
        if (db != 1 || len < 1 || len > 512) return;

        var value = new byte[len];
        lock (_gate)
        {
            if (off >= 0 && off + len <= _db.Length) Array.Copy(_db, off, value, 0, len);
        }
        await WriteDataAckAsync(stream, pduRef, value).ConfigureAwait(false);
    }

    private async Task ReplyWriteAsync(Stream stream, ushort pduRef, byte[] pdu, int paramLen)
    {
        if (pdu.Length < 10 + paramLen + 14) return;
        int i = 10 + paramLen;
        int db = (pdu[i + 8] << 8) | pdu[i + 9];
        int off = (pdu[i + 11] << 8) | pdu[i + 12];
        int len = (pdu[i + 4] << 8) | pdu[i + 5];
        if (db != 1 || len < 1 || len > 512) return;

        lock (_gate)
        {
            if (off >= 0 && off + len <= _db.Length && pdu.Length >= i + 14 + len)
                Array.Copy(pdu, i + 14, _db, off, len);
        }
        await WriteAckAsync(stream, pduRef).ConfigureAwait(false);
    }

    private async Task WriteConnectConfirmAsync(Stream stream)
    {
        var confirm = new byte[22];
        confirm[0] = 0x03; confirm[1] = 0x00;
        confirm[2] = 0x00; confirm[3] = 0x16;
        confirm[4] = 0x11; confirm[5] = 0xD0;
        confirm[6] = 0x00; confirm[7] = 0x01; confirm[8] = 0x00; confirm[9] = 0x01; confirm[10] = 0x00;
        confirm[11] = 0xC1; confirm[12] = 0x02; confirm[13] = 0x06; confirm[14] = 0x00;
        confirm[15] = 0xC2; confirm[16] = 0x02; confirm[17] = 0x06; confirm[18] = 0x00;
        confirm[19] = 0xC0; confirm[20] = 0x01; confirm[21] = 0x09;
        await stream.WriteAsync(confirm, _cts.Token).ConfigureAwait(false);
        await stream.FlushAsync(_cts.Token).ConfigureAwait(false);
    }

        private async Task WriteAckAsync(Stream stream, ushort pduRef)
    {
        var frame = BuildPduFrame(pduRef, 0x03, [], []);
        await stream.WriteAsync(frame, _cts.Token).ConfigureAwait(false);
        await stream.FlushAsync(_cts.Token).ConfigureAwait(false);
    }

    private async Task WriteDataAckAsync(Stream stream, ushort pduRef, byte[] value)
    {
        byte[] data;
        if (value.Length == 0)
            data = [0xFF, 0x04];
        else
            data = [0xFF, 0x04, (byte)value.Length, .. value];

        var frame = BuildPduFrame(pduRef, 0x03, [0x00, 0x04], data);
        await stream.WriteAsync(frame, _cts.Token).ConfigureAwait(false);
        await stream.FlushAsync(_cts.Token).ConfigureAwait(false);
    }

    private static byte[] BuildPduFrame(ushort pduRef, byte ros, byte[] param, byte[] data)
    {
        int pduLen = 10 + param.Length + data.Length;
        int total = 7 + pduLen;
        var frame = new byte[total];
        frame[0] = 0x03; frame[1] = 0x00;
        frame[2] = (byte)(total >> 8); frame[3] = (byte)total;   // TPKT
        frame[4] = 0x02; frame[5] = 0xF0; frame[6] = 0x80;       // COTP DT
        frame[7] = ros;
        frame[8] = 0x00; frame[9] = 0x00; frame[10] = 0x00;
        frame[11] = (byte)(pduRef >> 8); frame[12] = (byte)pduRef;
        frame[13] = (byte)(param.Length >> 8); frame[14] = (byte)param.Length;
        frame[15] = (byte)(data.Length >> 8); frame[16] = (byte)data.Length;
        Array.Copy(param, 0, frame, 17, param.Length);
        Array.Copy(data, 0, frame, 17 + param.Length, data.Length);
        return frame;
    }
}