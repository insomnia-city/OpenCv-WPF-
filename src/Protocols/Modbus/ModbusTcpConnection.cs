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

    private TcpClient? _client;
    private Stream? _stream;
    private static int _tid;

    public ModbusTcpConnection(string deviceId, string host, int port, byte unitId, ITagTable tagTable,
        TimeSpan? pollInterval = null)
    {
        DeviceId = deviceId;
        Host = host;
        Port = port;
        UnitId = unitId;
        TagTable = tagTable;
        PollInterval = pollInterval ?? TimeSpan.FromMilliseconds(200);
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

    public async Task ConnectAsync(CancellationToken ct)
    {
        lock (_stateGate)
        {
            if (_state == ConnectionState.Connected) return;
            _state = ConnectionState.Connecting;
        }
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
            lock (_stateGate) _state = ConnectionState.Disconnected;
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
            if (_stream is null) await ConnectAsync(ct).ConfigureAwait(false);
            var stream = _stream ?? throw new InvalidOperationException("connection lost");
            return await op(stream, ct).ConfigureAwait(false);
        }
        finally
        {
            _io.Release();
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
                        if (_stream is null) await ConnectAsync(ct).ConfigureAwait(false);
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