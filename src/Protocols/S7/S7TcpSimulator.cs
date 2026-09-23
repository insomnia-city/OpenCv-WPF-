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

    /// <summary>
    /// Force-closes every connected client — simulates a network drop for reconnect tests.
    /// Each dropped client triggers the per-session cleanup path; new connections still work.
    /// 强制断开所有已连接客户端(模拟网络中断，供重连测试)。连接清理路径复用；新连接不受影响。
    /// </summary>
    public void DropAll()
    {
        TcpClient[] victims;
        lock (_gate) victims = [.. _clients];
        foreach (var c in victims)
        {
            try { c.Dispose(); }
            catch { /* best-effort */ }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _listener.Stop();
        TcpClient[] victims;
        lock (_gate) { victims = [.. _clients]; _clients.Clear(); }
        foreach (var c in victims)
        {
            try { c.Dispose(); }
            catch { /* best-effort */ }
        }
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
        // S7 PDU header: pdu[0]=0x32 protocol, pdu[1]=ROSCTR, pdu[4..5]=ref, pdu[6..7]=param len.
        // A job REQUEST carries fn at pdu[10] and its 12-byte item at pdu[12..23] inside the params.
        // · S7 PDU 头: pdu[0]=0x32 协议、pdu[1]=ROSCTR、pdu[4..5]=引用号、pdu[6..7]=参数长。
        //   Job 请求在 pdu[10] 为功能码,12 字节 item 位于参数区内的 pdu[12..23]。
        if (pdu.Length < 24 || pdu[0] != S7Frame.ProtocolId) return;
        ushort pduRef = (ushort)((pdu[4] << 8) | pdu[5]);
        ushort paramLen = (ushort)((pdu[6] << 8) | pdu[7]);
        if (paramLen < 2 || pdu.Length < 10 + paramLen) return;
        byte fn = pdu[10];
        if (fn == S7Frame.FnRead)
            await ReplyReadAsync(stream, pduRef, pdu).ConfigureAwait(false);
        else if (fn == S7Frame.FnWrite)
            await ReplyWriteAsync(stream, pduRef, pdu).ConfigureAwait(false);
    }

    private async Task ReplyReadAsync(Stream stream, ushort pduRef, byte[] pdu)
    {
        const int item = 12; // header(10) + fn(1) + count(1) · 请求 item 起点
        if (pdu.Length < item + 12) return;
        int db = (pdu[item + 6] << 8) | pdu[item + 7];
        int off = ((pdu[item + 9] << 16) | (pdu[item + 10] << 8) | pdu[item + 11]) / 8;
        int len = (pdu[item + 4] << 8) | pdu[item + 5];
        if (db != 1 || len < 1 || len > 512) return;

        if (off < 0 || off + len > _db.Length)
        {
            // Address out of range (0x05 item error) — mirrors a real S7/PA link. · 地址越界(0x05 项错误),模拟真实链路应答。
            await WriteDataAckAsync(stream, pduRef, [0x05]).ConfigureAwait(false);
            return;
        }

        var value = new byte[len];
        lock (_gate) Array.Copy(_db, off, value, 0, len);
        int bits = len * 8;
        await WriteDataAckAsync(stream, pduRef, [0xFF, S7Frame.DataTransportByte, (byte)(bits >> 8), (byte)bits, .. value]).ConfigureAwait(false);
    }

    private async Task ReplyWriteAsync(Stream stream, ushort pduRef, byte[] pdu)
    {
        const int item = 12; // header(10) + fn(1) + count(1) · 请求 item 起点
        if (pdu.Length < item + 12) return;
        int db = (pdu[item + 6] << 8) | pdu[item + 7];
        int off = ((pdu[item + 9] << 16) | (pdu[item + 10] << 8) | pdu[item + 11]) / 8;
        int len = (pdu[item + 4] << 8) | pdu[item + 5];
        if (db != 1 || len < 1 || len > 512) return;

        // Data section of a write request starts at 10+paramLen: return code, transport,
        // bit length, then the payload bytes. · 写请求数据段自 10+paramLen 起:返回码、传输尺寸、位长、负载字节。
        int dataStart = 10 + ((pdu[6] << 8) | pdu[7]);
        if (pdu.Length < dataStart + 4 + len) return;
        if (off < 0 || off + len > _db.Length)
        {
            // Address out of range (0x05 item error). · 地址越界(0x05 项错误)。
            await WriteAckAsync(stream, pduRef, [0x05]).ConfigureAwait(false);
            return;
        }
        lock (_gate) Array.Copy(pdu, dataStart + 4, _db, off, len);
        await WriteAckAsync(stream, pduRef, [0xFF]).ConfigureAwait(false);
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

        private async Task WriteAckAsync(Stream stream, ushort pduRef, byte[] data)
    {
        var frame = BuildPduFrame(pduRef, S7Frame.FnWrite, data);
        await stream.WriteAsync(frame, _cts.Token).ConfigureAwait(false);
        await stream.FlushAsync(_cts.Token).ConfigureAwait(false);
    }

    private async Task WriteDataAckAsync(Stream stream, ushort pduRef, byte[] value)
    {
        var frame = BuildPduFrame(pduRef, S7Frame.FnRead, value);
        await stream.WriteAsync(frame, _cts.Token).ConfigureAwait(false);
        await stream.FlushAsync(_cts.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds one S7 ack-data telegram (TPKT + COTP DT + PDU) in canonical layout:
    /// 12-byte response header (protocol 0x32, ROSCTR 0x03, ref, param len, data len,
    /// error class/code), then the 2-byte param (fn + item count) and the data item.
    /// / 组装一帧标准 S7 应答数据报文(TPKT+COTP DT+PDU):12 字节响应头(协议 0x32、
    ///   ROSCTR 0x03、引用号、参数长、数据长、错误类/码),随后 2 字节参数(功能码+条目数)与数据项。
    /// </summary>
    private static byte[] BuildPduFrame(ushort pduRef, byte fn, byte[] data)
    {
        int pduLen = 12 + 2 + data.Length;
        int total = 7 + pduLen;
        var frame = new byte[total];
        frame[0] = 0x03; frame[1] = 0x00;
        frame[2] = (byte)(total >> 8); frame[3] = (byte)total;   // TPKT
        frame[4] = 0x02; frame[5] = 0xF0; frame[6] = 0x80;       // COTP DT
        frame[7] = S7Frame.ProtocolId;                           // 0x32
        frame[8] = 0x03;                                         // ROSCTR ack-data
        frame[9] = 0x00; frame[10] = 0x00;                       // reserved
        frame[11] = (byte)(pduRef >> 8); frame[12] = (byte)pduRef;
        frame[13] = (byte)(2 >> 8); frame[14] = (byte)2;         // param len = fn + count
        frame[15] = (byte)(data.Length >> 8); frame[16] = (byte)data.Length;
        frame[17] = 0x00; frame[18] = 0x00;                      // error class + error code
        frame[19] = fn; frame[20] = 0x01;                        // fn + item count
        Array.Copy(data, 0, frame, 21, data.Length);
        return frame;
    }
}