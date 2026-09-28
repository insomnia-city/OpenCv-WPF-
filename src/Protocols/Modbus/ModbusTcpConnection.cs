using System.Net.Sockets;
using HalconWorkflow.Abstractions;

namespace HalconWorkflow.Protocols.Modbus;

/// <summary>
/// Modbus TCP client adapter (pure .NET MBAP framing; no external stack). Tag
/// addresses resolve through <see cref="ITagTable"/> to register addresses such as
/// "coil:0" / "holding:0". Every request is serialized and cancelled with the token.
/// / Modbus TCP 客户端适配器(纯 .NET MBAP 组帧，无第三方栈)。Tag 经 ITagTable 解析为
///   寄存器地址如 "coil:0" / "holding:0"。请求串行化，支持取消令牌。
/// </summary>
public sealed class ModbusTcpConnection : IDeviceConnection
{
    private readonly SemaphoreSlim _io = new(1, 1);
    private readonly object _stateGate = new();
    private readonly HashSet<(string Pattern, CancellationTokenSource Cts)> _subs = [];
    private readonly ReconnectOptions _reconnect;

    private TcpClient? _client;
    private Stream? _stream;
    private static int _tid;

    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly object _recoveryGate = new();
    private Task? _recoveryTask;
    private bool _recovering;
    private long _reconnectCount;

    public ModbusTcpConnection(string deviceId, string host, int port, byte unitId, ITagTable tagTable,
        TimeSpan? pollInterval = null, ReconnectOptions? reconnect = null)
    {
        DeviceId = deviceId;
        Host = host;
        Port = port;
        UnitId = unitId;
        TagTable = tagTable;
        PollInterval = pollInterval ?? TimeSpan.FromMilliseconds(200);
        _reconnect = reconnect ?? new ReconnectOptions();
        if (_reconnect.HeartbeatMs > 0)
            _ = HeartbeatLoopAsync();
    }

    public string ProtocolId => "modbus";
    public string DeviceId { get; }
    public string Host { get; }
    public int Port { get; }
    public byte UnitId { get; }
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
    public bool IsRecovering
    {
        get
        {
            lock (_recoveryGate) return _recovering;
        }
    }

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
            throw new InvalidOperationException($"modbus connect {DeviceId}@{Host}:{Port} failed: {ex.Message}", ex);
        }
    }

    /// <see cref="IDeviceConnection.ReadAsync"/>
    public Task<object> ReadAsync(string tag, CancellationToken ct)
    {
        var (addr, _) = ResolveRead(tag);
        return IoAsync(ReadOne(addr, tag), ct);
    }

    /// <see cref="IDeviceConnection.WriteAsync"/>
    public Task WriteAsync(string tag, object value, CancellationToken ct)
    {
        var (addr, entry) = ResolveWrite(tag);
        var payload = addr.WriteFunctionCode == 0x05 ? CoilPayload(value) : HoldingPayload(value);
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
        // Serialize Modbus transactions: a device answers one request at a time, so
        // concurrent callers must not interleave requests onto one socket.
        // / 串行化 Modbus 事务：设备逐条应答，并发必须排他。
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
            // Transport fault → the connection is dead; recovery (if enabled) is the only
            // path forward. Go straight to Reconnecting rather than staging through
            // Disconnected, which exposed a window where IsRecovering was already true but
            // State still said Disconnected (observers polling one then asserting the other
            // read an incoherent snapshot on slow hardware).
            // · 传输故障→连接已死;恢复(若启用)是唯一出路。直接切 Reconnecting,避免先落
            // Disconnected 暴露窗口(IsRecovering 已 true 而 State 仍是 Disconnected)。
            _state = ConnectionState.Reconnecting;
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
                    // Heal is the end of recovery: clear IsRecovering and State together so an
                    // observer never sees State==Connected while the flag is still set.
                    // · 愈合即恢复结束:IsRecovering 与 State 一并清除,避免观察者看到 State==Connected 时标志仍置位。
                    lock (_recoveryGate)
                    {
                        _recovering = false;
                        StateWait(ConnectionState.Connected);
                    }
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
                    // Give-up is the end of recovery: clear IsRecovering and State together so
                    // an observer never sees State==Disconnected while the flag is still set.
                    // · 放弃即恢复结束:IsRecovering 与 State 一并清除,避免观察者看到 State==Disconnected 时标志仍置位。
                    lock (_recoveryGate)
                    {
                        _recovering = false;
                        StateWait(ConnectionState.Disconnected);
                    }
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

    private static Func<Stream, CancellationToken, Task<object>> ReadOne(ModbusRegisterAddress addr, string tag)
        => (s, ct) => RoundTripAsync(s, addr.ReadFunctionCode,
            [(byte)(addr.Index >> 8), (byte)addr.Index, 0, 1],
            pdu => DecodeOne(addr, pdu), ct);

    private static Func<Stream, CancellationToken, Task<int>> WriteOne(ModbusRegisterAddress addr, byte[] payload)
        => (s, ct) => RoundTripAsync(s, addr.WriteFunctionCode!.Value,
            [(byte)(addr.Index >> 8), (byte)addr.Index, payload[0], payload[1]],
            _ => 0, ct);

    private (ModbusRegisterAddress Addr, TagTableEntry Entry) ResolveRead(string tag)
    {
        var entry = ResolveOrThrow(tag);
        if (!entry.Readable) throw new InvalidOperationException($"tag '{tag}' is not readable");
        return (ModbusRegisterAddress.Parse(entry.ProtocolAddress), entry);
    }

    private (ModbusRegisterAddress Addr, TagTableEntry Entry) ResolveWrite(string tag)
    {
        var entry = ResolveOrThrow(tag);
        if (!entry.Writable) throw new InvalidOperationException($"tag '{tag}' is not writable");
        var addr = ModbusRegisterAddress.Parse(entry.ProtocolAddress);
        if (addr.WriteFunctionCode is null)
            throw new NotSupportedException($"area {addr.Area} is read-only");
        return (addr, entry);
    }

    private static async Task<TResult> RoundTripAsync<TResult>(Stream s, byte fc, byte[] pdu, Func<byte[], TResult> decode, CancellationToken ct)
    {
        var tid = (ushort)Interlocked.Increment(ref _tid);
        var frame = ModbusFrame.BuildRequest(tid, 0xFF, [fc, .. pdu]);
        await s.WriteAsync(frame, ct).ConfigureAwait(false);
        await s.FlushAsync(ct).ConfigureAwait(false);
        var resp = await ModbusFrame.ReadAduPduAsync(s, tid, 0xFF, ct).ConfigureAwait(false);
        if (resp.Length < 1) throw new InvalidDataException("empty modbus PDU response");
        if (resp[0] == (fc | 0x80))
            throw new ModbusException(fc, resp.Length > 1 ? resp[1] : (byte)0);
        if (resp[0] != fc)
            throw new InvalidDataException($"modbus function mismatch: sent {fc:X2} got {resp[0]:X2}");
        return decode(resp);
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

    private static object DecodeOne(ModbusRegisterAddress addr, byte[] pdu)
    {
        if (addr.Area is ModbusArea.Coil or ModbusArea.Discrete)
        {
            if (pdu.Length < 3 || pdu[1] < 1) throw new InvalidDataException("bad bit response");
            return (pdu[2] & 1) != 0;
        }

        if (pdu.Length < 3 || pdu[1] < 2) throw new InvalidDataException("bad word response");
        return (ushort)((pdu[2] << 8) | pdu[3]);
    }

    private static byte[] CoilPayload(object value)
    {
        bool b = value is bool bv ? bv : Convert.ToBoolean(value);
        return b ? [0xFF, 0x00] : [0x00, 0x00];
    }

    private static byte[] HoldingPayload(object value)
    {
        ushort v = Convert.ToUInt16(value);
        return [(byte)(v >> 8), (byte)v];
    }
}

/// <summary>
/// Minimal ordered single-subscriber observable buffer used by <see cref="Subscribe"/>.
/// / 极简有序的单订阅缓冲，供 Subscribe 使用。
/// </summary>
internal sealed class SubjectBuffer : IObservable<TagValue>, IDisposable
{
    private readonly List<TagValue> _pending = [];
    private IObserver<TagValue>? _observer;
    private bool _done;

    public IDisposable Subscribe(IObserver<TagValue> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        lock (_pending)
        {
            _observer ??= observer;
            foreach (var v in _pending) observer.OnNext(v);
            _pending.Clear();
        }
        return this;
    }

    public void Push(TagValue v)
    {
        lock (_pending)
        {
            if (_observer is { } o && !_done) o.OnNext(v);
            else _pending.Add(v);
        }
    }

    public void Done()
    {
        lock (_pending)
        {
            if (_observer is { } o && !_done) o.OnCompleted();
            _done = true;
        }
    }

    public void Dispose() { }
}