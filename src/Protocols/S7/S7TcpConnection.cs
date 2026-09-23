using System.Buffers.Binary;
using System.Net.Sockets;
using HalconWorkflow.Abstractions;
using HalconWorkflow.Protocols.Modbus;

namespace HalconWorkflow.Protocols.S7;

/// <summary>
/// S7 ISO-on-TCP client adapter (pure .NET TPKT/COTP + S7comm framing; no external
/// stack). Tag addresses resolve through <see cref="ITagTable"/> to DB byte-range
/// addresses such as "db:0" / "db2:16" (§13.1 stage-22 S7 P0). One request runs on
/// the wire at a time; transport failures drive the same recovery as the Modbus twin.
/// · S7 ISO-on-TCP 客户端适配器(纯 .NET TPKT/COTP + S7comm 组帧,无第三方栈)。Tag
///   经 ITagTable 解析为 DB 字节区间地址,如 "db:0" / "db2:16"(§13.1 阶段22 S7 P0)。
///   每条请求串行上总线;传输故障走与 Modbus 对偶相同的恢复逻辑。
/// </summary>
public sealed class S7TcpConnection : IDeviceConnection
{
    private readonly SemaphoreSlim _io = new(1, 1);
    private readonly object _stateGate = new();
    private readonly HashSet<(string Pattern, CancellationTokenSource Cts)> _subs = [];
    private readonly ReconnectOptions _reconnect;

    private TcpClient? _client;
    private Stream? _stream;
    private static int _seq;

    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly object _recoveryGate = new();
    private Task? _recoveryTask;
    private bool _recovering;
    private long _reconnectCount;

    public S7TcpConnection(string deviceId, string host, int port, ITagTable tagTable,
        TimeSpan? pollInterval = null, ReconnectOptions? reconnect = null)
    {
        DeviceId = deviceId;
        Host = host;
        Port = port;
        TagTable = tagTable;
        PollInterval = pollInterval ?? TimeSpan.FromMilliseconds(200);
        _reconnect = reconnect ?? new ReconnectOptions();
        if (_reconnect.HeartbeatMs > 0)
            _ = HeartbeatLoopAsync();
    }

    public string ProtocolId => "s7";
    public string DeviceId { get; }
    public string Host { get; }
    public int Port { get; }
    public ITagTable TagTable { get; }
    public TimeSpan PollInterval { get; }

    public ConnectionState State
    {
        get
        {
            lock (_stateGate) return _state;
        }
    }
    private ConnectionState _state = ConnectionState.Disconnected;

    /// <summary>True while the background recovery loop owns connecting. · 后台恢复循环正在负责连接时为真</summary>
    public bool IsRecovering => _recovering;

    /// <summary>Successful reconnects after a transport failure (heartbeat or I/O). · 传输故障后成功重连次数(心跳或 I/O)</summary>
    public long ReconnectCount => Interlocked.Read(ref _reconnectCount);

    /// <summary>Message of the last failed reconnect attempt, null after success. · 最近一次失败重连的消息,成功后为 null</summary>
    public string? LastError { get; private set; }

    public async Task ConnectAsync(CancellationToken ct)
    {
        if (!_recovering)
        {
            lock (_stateGate)
            {
                if (_state == ConnectionState.Connected) return;
                _state = ConnectionState.Connecting;
            }
        }
        await ConnectCoreAsync(ct).ConfigureAwait(false);
    }

    private async Task ConnectCoreAsync(CancellationToken ct)
    {
        try
        {
            var client = new TcpClient();
            await client.ConnectAsync(Host, Port, ct).ConfigureAwait(false);
            _client = client;
            _stream = client.GetStream();
            var cr = S7Frame.BuildConnectRequest();
            await _stream.WriteAsync(cr, ct).ConfigureAwait(false);
            await _stream.FlushAsync(ct).ConfigureAwait(false);
            await S7Frame.ReadConnectConfirmAsync(_stream, ct).ConfigureAwait(false);
            lock (_stateGate) _state = ConnectionState.Connected;
        }
        catch (Exception ex)
        {
            _client?.Dispose();
            _client = null;
            _stream = null;
            // Recovery owns the state while reconnecting; don't flip to Disconnected mid-session.
            // · 重连中由恢复循环独占状态,不要在会话中途翻回 Disconnected
            if (!_recovering) StateWait(ConnectionState.Disconnected);
            throw new InvalidOperationException($"s7 connect {DeviceId}@{Host}:{Port} failed: {ex.Message}", ex);
        }
    }

    /// <see cref="IDeviceConnection.ReadAsync"/>
    public Task<object> ReadAsync(string tag, CancellationToken ct)
    {
        var (addr, entry) = ResolveRead(tag);
        return IoAsync(ReadOne(addr, WidthOf(entry.DataType), entry.DataType), ct);
    }

    /// <see cref="IDeviceConnection.WriteAsync"/>
    public Task WriteAsync(string tag, object value, CancellationToken ct)
    {
        var (addr, entry) = ResolveWrite(tag);
        var payload = Encode(value, entry.DataType);
        return IoAsync(WriteOne(addr, payload), ct);
    }

    /// <summary>
    /// Change-detection subscription: a background poller reads every matching tag at
    /// <see cref="PollInterval"/> and pushes on value change (quality Good/Stale).
    /// / 变化检测订阅：后台轮询器按 PollInterval 读取每个匹配 Tag，值变化即推送。
    /// </summary>
    public IObservable<TagValue> Subscribe(string tagsPattern)
    {
        var subject = new SubjectBuffer();
        lock (_stateGate)
        {
            var cts = new CancellationTokenSource();
            _subs.Add((tagsPattern, cts));
            _ = PollUntilCancelledAsync(tagsPattern, subject, cts.Token);
            return subject;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _lifetimeCts.Cancel();
        var recovery = _recoveryTask;
        if (recovery is not null)
        {
            try { await recovery.ConfigureAwait(false); }
            catch { /* best effort · 尽力而为 */ }
        }
        List<CancellationTokenSource>? subs;
        lock (_stateGate)
        {
            subs = _subs.Select(s => s.Cts).ToList();
            _subs.Clear();
        }
        foreach (var cts in subs) cts.Cancel();
        var stream = _stream;
        var client = _client;
        _stream = null;
        _client = null;
        if (stream is not null) await stream.DisposeAsync().ConfigureAwait(false);
        client?.Dispose();
        _lifetimeCts.Dispose();
        lock (_stateGate) _state = ConnectionState.Disconnected;
        _io.Dispose();
    }

    private async Task<T> IoAsync<T>(Func<Stream, CancellationToken, Task<T>> op, CancellationToken ct)
    {
        // Serialize S7 transactions: a device answers one request at a time, so
        // concurrent callers must not interleave requests onto one socket.
        // · 串行化 S7 事务:设备逐条应答,并发必须排他。
        await _io.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_recovering)
                throw new InvalidOperationException(
                    $"device '{DeviceId}' is reconnecting. · 设备 '{DeviceId}' 正在重连。");
            if (_stream is null) await ConnectAsync(ct).ConfigureAwait(false);
            var stream = _stream ?? throw new InvalidOperationException("connection lost");
            return await op(stream, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsTransportFailure(ex))
        {
            FaultTransport();
            throw;
        }
        finally
        {
            _io.Release();
        }
    }

    private static bool IsTransportFailure(Exception ex) =>
        ex is IOException or SocketException or ObjectDisposedException;

    /// <summary>
    /// Tears down the broken socket and, when recovery is enabled, starts the background
    /// reconnect loop with exponential backoff. · 拆除故障套接字;开启恢复时按指数退避启动后台重连。
    /// </summary>
    private void FaultTransport()
    {
        TcpClient? client;
        Stream? stream;
        lock (_stateGate)
        {
            stream = _stream;
            _stream = null;
            client = _client;
            _client = null;
            if (_state == ConnectionState.Connected) _state = ConnectionState.Disconnected;
        }
        stream?.Dispose();
        client?.Dispose();

        lock (_recoveryGate)
        {
            if (_recovering) return;
            _recovering = true;
        }
        _recoveryTask = Task.Run(RecoverLoopAsync);
    }

    private async Task RecoverLoopAsync()
    {
        StateWait(ConnectionState.Reconnecting);
        var attempt = 0;
        long delay = Math.Max(1, _reconnect.InitialDelayMs);
        try
        {
            while (true)
            {
                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
                attemptCts.CancelAfter(Math.Max(1, _reconnect.ConnectTimeoutMs));
                try
                {
                    await ConnectCoreAsync(attemptCts.Token).ConfigureAwait(false);
                    Interlocked.Increment(ref _reconnectCount);
                    LastError = null;
                    lock (_recoveryGate) _recovering = false;
                    StateWait(ConnectionState.Connected);
                    return;
                }
                catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                }

                attempt++;
                if (_reconnect.MaxAttempts > 0 && attempt >= _reconnect.MaxAttempts)
                {
                    lock (_recoveryGate) _recovering = false;
                    StateWait(ConnectionState.Disconnected);
                    return;
                }

                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(delay), _lifetimeCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                delay = Math.Min((long)(delay * Math.Max(1.0, _reconnect.Multiplier)), Math.Max(1, _reconnect.MaxDelayMs));
            }
        }
        finally
        {
            lock (_recoveryGate) _recovering = false;
        }
    }

    private void StateWait(ConnectionState state)
    {
        lock (_stateGate) _state = state;
    }

    private async Task HeartbeatLoopAsync()
    {
        var probeTag = TagTable.All().FirstOrDefault(t => t.Readable)?.Tag;
        if (probeTag is null) return;
        try
        {
            while (!_lifetimeCts.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(50, _reconnect.HeartbeatMs)), _lifetimeCts.Token)
                    .ConfigureAwait(false);
                if (_recovering) continue;
                lock (_stateGate)
                {
                    // The change-poller already covers liveness when subscribed. · 有轮询订阅时由轮询器负责存活
                    if (_subs.Count > 0) continue;
                    if (_state != ConnectionState.Connected) continue;
                }
                try
                {
                    using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
                    probeCts.CancelAfter(Math.Max(500, _reconnect.HeartbeatMs * 2));
                    await ReadAsync(probeTag, probeCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!_lifetimeCts.IsCancellationRequested)
                {
                    // Silent drop: the peer never answered within the probe deadline. · 静默断开:超时无应答
                    FaultTransport();
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex) when (IsTransportFailure(ex))
                {
                    FaultTransport();
                }
                catch (Exception)
                {
                    // Protocol-level errors are not liveness failures. · 协议级错误不算存活故障
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutdown · 释放
        }
    }

    private static Func<Stream, CancellationToken, Task<object>> ReadOne(S7DbAddress addr, int byteCount, Type dataType)
        => (s, ct) =>
        {
            var seq = NextSeq();
            return RoundTripAsync(s,
                () => S7Frame.BuildRead(seq, addr, byteCount),
                pdu => Decode(S7Frame.ExtractReadResult(pdu, seq), dataType), ct);
        };

    private static Func<Stream, CancellationToken, Task<int>> WriteOne(S7DbAddress addr, byte[] payload)
        => (s, ct) =>
        {
            var seq = NextSeq();
            return RoundTripAsync(s,
                () => S7Frame.BuildWrite(seq, addr, payload),
                pdu => { S7Frame.VerifyWriteAck(pdu, seq); return 0; }, ct);
        };

    private (S7DbAddress Addr, TagTableEntry Entry) ResolveRead(string tag)
    {
        var entry = ResolveOrThrow(tag);
        if (!entry.Readable) throw new InvalidOperationException($"tag '{tag}' is not readable");
        return (S7DbAddress.Parse(entry.ProtocolAddress), entry);
    }

    private (S7DbAddress Addr, TagTableEntry Entry) ResolveWrite(string tag)
    {
        var entry = ResolveOrThrow(tag);
        if (!entry.Writable) throw new InvalidOperationException($"tag '{tag}' is not writable");
        return (S7DbAddress.Parse(entry.ProtocolAddress), entry);
    }

    private static ushort NextSeq() => (ushort)(Interlocked.Increment(ref _seq) & 0xFFFF);

    private static async Task<TResult> RoundTripAsync<TResult>(Stream s, Func<byte[]> build, Func<byte[], TResult> decode, CancellationToken ct)
    {
        var frame = build();
        await s.WriteAsync(frame, ct).ConfigureAwait(false);
        await s.FlushAsync(ct).ConfigureAwait(false);
        var pdu = await S7Frame.ReadPduAsync(s, ct).ConfigureAwait(false);
        return decode(pdu);
    }

    private TagTableEntry ResolveOrThrow(string tag)
    {
        var entry = TagTable.Resolve(tag);
        if (entry is null)
            throw new ArgumentException($"unknown tag '{tag}' (register it in the tag table first)");
        if (!string.Equals(entry.DeviceId, DeviceId, StringComparison.Ordinal))
            throw new ArgumentException($"tag '{tag}' belongs to device '{entry.DeviceId}', not '{DeviceId}'");
        return entry;
    }

    private async Task PollUntilCancelledAsync(string pattern, SubjectBuffer subject, CancellationToken ct)
    {
        var cache = new Dictionary<string, object?>(StringComparer.Ordinal);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                foreach (var entry in TagTable.All())
                {
                    if (!Matches(pattern, entry.Tag)) continue;
                    bool changed = false;
                    object? value = null;
                    var quality = Quality.Good;
                    try
                    {
                        if (_stream is null && !_recovering) await ConnectAsync(ct).ConfigureAwait(false);
                        value = await ReadAsync(entry.Tag, ct).ConfigureAwait(false);
                        changed = !cache.TryGetValue(entry.Tag, out var old) || !Equals(old, value);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception)
                    {
                        quality = Quality.Stale;
                        changed = cache.ContainsKey(entry.Tag);
                    }
                    if (changed)
                    {
                        cache[entry.Tag] = value;
                        subject.Push(new TagValue(entry.Tag, value, DateTimeOffset.Now, quality));
                    }
                }
                await Task.Delay(PollInterval, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // normal unsubscribe / dispose · 正常退订/释放
        }
        finally
        {
            subject.Done();
        }
    }

    internal static bool Matches(string pattern, string tag)
    {
        if (pattern == "*") return true;
        if (pattern.EndsWith('*')) return tag.StartsWith(pattern[..^1], StringComparison.Ordinal);
        return string.Equals(pattern, tag, StringComparison.Ordinal);
    }

    /// <summary>Byte width of one CLR tag type on the S7 wire (big-endian). · 一种 CLR Tag 类型在 S7 总线上的字节宽(大端)</summary>
    private static int WidthOf(Type t) => t switch
    {
        _ when t == typeof(bool) || t == typeof(byte) => 1,
        _ when t == typeof(short) || t == typeof(ushort) => 2,
        _ when t == typeof(int) || t == typeof(uint) || t == typeof(float) => 4,
        _ when t == typeof(long) || t == typeof(ulong) || t == typeof(double) => 8,
        _ => throw new NotSupportedException(
            $"s7 tag type '{t.Name}' is not supported this stage; use bool/byte/short/ushort/int/uint/long/ulong/float/double"),
    };

    private static byte[] Encode(object value, Type t)
    {
        if (t == typeof(bool)) return [Convert.ToBoolean(value) ? (byte)1 : (byte)0];
        if (t == typeof(byte)) return [Convert.ToByte(value)];
        if (t == typeof(short)) { var b = new byte[2]; BinaryPrimitives.WriteInt16BigEndian(b, Convert.ToInt16(value)); return b; }
        if (t == typeof(ushort)) { var b = new byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, Convert.ToUInt16(value)); return b; }
        if (t == typeof(int)) { var b = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(b, Convert.ToInt32(value)); return b; }
        if (t == typeof(uint)) { var b = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, Convert.ToUInt32(value)); return b; }
        if (t == typeof(float)) { var b = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, BitConverter.SingleToUInt32Bits(Convert.ToSingle(value))); return b; }
        if (t == typeof(long)) { var b = new byte[8]; BinaryPrimitives.WriteInt64BigEndian(b, Convert.ToInt64(value)); return b; }
        if (t == typeof(ulong)) { var b = new byte[8]; BinaryPrimitives.WriteUInt64BigEndian(b, Convert.ToUInt64(value)); return b; }
        if (t == typeof(double)) { var b = new byte[8]; BinaryPrimitives.WriteUInt64BigEndian(b, BitConverter.DoubleToUInt64Bits(Convert.ToDouble(value))); return b; }
        throw new NotSupportedException($"s7 tag type '{t.Name}' is not supported this stage (byte-range types only)");
    }

    private static object Decode(byte[] data, Type t)
    {
        if (t == typeof(bool)) return data.Length > 0 && data[0] != 0;
        if (t == typeof(byte)) return data.Length > 0 ? data[0] : (byte)0;
        if (t == typeof(short)) return Length(data, 2) ? BinaryPrimitives.ReadInt16BigEndian(data) : (short)0;
        if (t == typeof(ushort)) return Length(data, 2) ? BinaryPrimitives.ReadUInt16BigEndian(data) : (ushort)0;
        if (t == typeof(int)) return Length(data, 4) ? BinaryPrimitives.ReadInt32BigEndian(data) : 0;
        if (t == typeof(uint)) return Length(data, 4) ? BinaryPrimitives.ReadUInt32BigEndian(data) : (uint)0;
        if (t == typeof(float)) return Length(data, 4) ? BitConverter.UInt32BitsToSingle(BinaryPrimitives.ReadUInt32BigEndian(data)) : 0f;
        if (t == typeof(long)) return Length(data, 8) ? BinaryPrimitives.ReadInt64BigEndian(data) : (long)0;
        if (t == typeof(ulong)) return Length(data, 8) ? BinaryPrimitives.ReadUInt64BigEndian(data) : (ulong)0;
        if (t == typeof(double)) return Length(data, 8) ? BitConverter.UInt64BitsToDouble(BinaryPrimitives.ReadUInt64BigEndian(data)) : 0d;
        throw new NotSupportedException($"s7 tag type '{t.Name}' is not supported this stage (byte-range types only)");
    }

    private static bool Length(byte[] data, int min) => data.Length >= min;
}