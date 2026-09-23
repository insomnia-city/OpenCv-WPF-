using System.Net;
using System.Net.Sockets;
using HalconWorkflow.Abstractions;
using HalconWorkflow.Protocols;
using HalconWorkflow.Protocols.Devices;
using HalconWorkflow.Protocols.Modbus;
using HalconWorkflow.Protocols.S7;
using Xunit;

namespace HalconWorkflow.Protocols.Tests;

/// <summary>
/// Stage-22 S7 P0 closed loop (ADR-026): canonical S7comm framing plus the client/simulator
/// twin over real TPKT/COTP sockets (§13.1). Covers address parsing, the framer, DB read/write
/// round trips for every supported CLR type, catalog wiring and the reconnect/hb behavior that
/// the Modbus stage established.
/// · 阶段22 S7 P0 闭环(ADR-026):正典 S7comm 组帧与 客户/模拟 孪生经真实 TPKT/COTP 套接字
///   (§13.1)。覆盖地址解析、组帧、各 CLR 类型的 DB 读写往返、目录接线与 Modbus 阶段确立的
///   重连/心跳行为。
/// </summary>
public class S7Tests
{
    private sealed class BlackholeSlave : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cts = new();
        private readonly Socket[] _accepted = new Socket[8];
        private int _count;
        private Task? _loop;
        public int Port { get; }

        public BlackholeSlave()
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        }

        public void Start() => _loop = AcceptLoopAsync();

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var socket = await _listener.AcceptSocketAsync(_cts.Token).ConfigureAwait(false);
                    int i = Interlocked.Increment(ref _count) - 1;
                    if (i < _accepted.Length) _accepted[i] = socket;
                    _ = Task.Run(() => AnswerConnectOnlyAsync(socket));
                }
            }
            catch (OperationCanceledException) { }
        }

        /// <summary>
        /// Answers the COTP Connection Request (so <see cref="S7TcpConnection.ConnectAsync"/>
        /// succeeds) but never answers job PDUs: a silent drop after the handshake.
        /// · 只应答 COTP 连接请求(使 ConnectAsync 成功),之后对作业 PDU 一律不应答:握手后的静默黑洞。
        /// </summary>
        private static async Task AnswerConnectOnlyAsync(Socket s)
        {
            var buf = new byte[512];
            try
            {
                bool connected = false;
                while (!connected)
                {
                    int n = await s.ReceiveAsync(buf).ConfigureAwait(false);
                    if (n < 4) return;
                    if (n >= 6 && buf[4] == 0x11 && buf[5] == 0xE0)
                    {
                        await s.SendAsync(ConnectConfirm).ConfigureAwait(false);
                        connected = true;
                    }
                }
                // Drain subsequent telegrams silently. · 后续报文只读不应答
                while (await s.ReceiveAsync(buf).ConfigureAwait(false) > 0) { }
            }
            catch { }
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { }
            foreach (var s in _accepted) { try { s.Dispose(); } catch { } }
            await Task.CompletedTask;
        }
    }

    private static readonly byte[] ConnectConfirm =
    [
        0x03, 0x00, 0x00, 0x16,
        0x11, 0xD0, 0x00, 0x01, 0x00, 0x01, 0x00,
        0xC1, 0x02, 0x00, 0x01,
        0xC2, 0x02, 0x00, 0x02,
        0xC0, 0x01, 0x09,
    ];

    private sealed class Collector : IObserver<TagValue>
    {
        private readonly List<TagValue> _items;

        public Collector(List<TagValue> items)
        {
            _items = items;
        }

        public void OnNext(TagValue value) => _items.Add(value);

        public void OnError(Exception error) { }

        public void OnCompleted() { }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
                throw new TimeoutException($"Condition not met within {timeoutMs}ms. · 条件 {timeoutMs}ms 内未满足");
            await Task.Delay(25);
        }
    }

    private static (TagTable Tags, S7TcpConnection Conn) Build(string deviceId, int port, TimeSpan? poll = null, ReconnectOptions? reconnect = null)
    {
        var tags = new TagTable();
        tags.Register(new($"{deviceId}/u16", deviceId, "db:0", typeof(ushort), true, true));
        tags.Register(new($"{deviceId}/i32", deviceId, "db:2", typeof(int), true, true));
        tags.Register(new($"{deviceId}/f32", deviceId, "db:6", typeof(float), true, true));
        tags.Register(new($"{deviceId}/f64", deviceId, "db:10", typeof(double), true, true));
        tags.Register(new($"{deviceId}/b", deviceId, "db:18", typeof(bool), true, true));
        tags.Register(new($"{deviceId}/h08", deviceId, "db:19", typeof(long), true, true));
        tags.Register(new($"{deviceId}/r", deviceId, "db:300", typeof(ushort), true, true));
        tags.Register(new($"{deviceId}/non", deviceId, "db:80", typeof(string), true, true));
        var conn = new S7TcpConnection(deviceId, "127.0.0.1", port, tags, poll, reconnect);
        return (tags, conn);
    }

    // ----- S7DbAddress parsing · 地址解析 -----

    [Fact]
    public void DbAddress_Parse_SupportsDefaultsAndExplicitDb()
    {
        Assert.Equal(1, S7DbAddress.Parse("db:0").DbNumber);
        Assert.Equal(0, S7DbAddress.Parse("db:0").ByteOffset);
        Assert.Equal(2, S7DbAddress.Parse("db2:16").DbNumber);
        Assert.Equal(16, S7DbAddress.Parse("db2:16").ByteOffset);
        Assert.Equal(16 * 8, S7DbAddress.Parse("db:16").BitAddress);
    }

    [Theory]
    [InlineData("")]
    [InlineData(":0")]
    [InlineData("db:")]
    [InlineData("db0:1")]
    [InlineData("db:-1")]
    [InlineData("holding:0")]
    [InlineData("db:abc")]
    public void DbAddress_Parse_RejectsMalformed(string address)
    {
        Assert.Throws<FormatException>(() => S7DbAddress.Parse(address));
    }

    // ----- Framer layout · 帧结构 -----

    [Fact]
    public void BuildRead_CanonicalHeaderItemAndTelegram()
    {
        var frame = S7Frame.BuildRead(0x0100, S7DbAddress.Parse("db:2"), 2);
        Assert.Equal(7 + (10 + 14), (frame[2] << 8) | frame[3]); // TPKT length · TPKT 总长
        var pdu = frame[7..];
        Assert.Equal(S7Frame.ProtocolId, pdu[0]);
        Assert.Equal(S7Frame.Job, pdu[1]);
        Assert.Equal((byte)0x01, pdu[4]);
        Assert.Equal((byte)0x00, pdu[5]); // seq 0x0100 · 序号
        Assert.Equal((byte)0x00, pdu[6]);
        Assert.Equal((byte)0x0E, pdu[7]); // param len 14 · 参数长
        Assert.Equal((byte)0x00, pdu[8]);
        Assert.Equal((byte)0x00, pdu[9]); // data len 0 · 数据长
        Assert.Equal(S7Frame.FnRead, pdu[10]);
        Assert.Equal((byte)0x01, pdu[11]);
        var item = pdu[12..];
        Assert.Equal(S7Frame.ItemSpec, item[0]);
        Assert.Equal(S7Frame.ItemSpecLen, item[1]);
        Assert.Equal(S7Frame.SyntaxS7Any, item[2]);
        Assert.Equal(S7Frame.TransportByte, item[3]);
        Assert.Equal((byte)0x00, item[4]);
        Assert.Equal((byte)0x02, item[5]); // byte length · 字节数
        Assert.Equal((byte)0x00, item[6]);
        Assert.Equal((byte)0x01, item[7]); // db 1 · DB 号
        Assert.Equal(S7Frame.AreaDb, item[8]);
        Assert.Equal((byte)0x00, item[9]);
        Assert.Equal((byte)0x00, item[10]);
        Assert.Equal((byte)0x10, item[11]); // 24-bit bit address 0x000010 = 2*8 · 24 位位地址
    }

    [Fact]
    public void BuildWrite_CanonicalDataItemCarriesValue()
    {
        var frame = S7Frame.BuildWrite(0x0001, S7DbAddress.Parse("db:4"), [(byte)0xAB, (byte)0xCD]);
        var pdu = frame[7..];
        Assert.Equal((byte)0x05, pdu[10]); // write function · 写功能
        Assert.Equal((byte)0x00, pdu[8]);
        Assert.Equal((byte)0x06, pdu[9]); // data len 6 = 4 + 2 bytes · 数据长
        var data = pdu[24..];
        Assert.Equal((byte)0x00, data[0]);
        Assert.Equal(S7Frame.DataTransportByte, data[1]);
        Assert.Equal((byte)0x00, data[2]);
        Assert.Equal((byte)0x10, data[3]); // 16 bits for 2 bytes · 2 字节=16 位
        Assert.Equal((byte)0xAB, data[4]);
        Assert.Equal((byte)0xCD, data[5]);
    }

    // ----- DB read/write round trips over a real loopback socket · 真实回环套接字上的 DB 读写往返 -----

    [Fact]
    public async Task RoundTrip_EverySupportedTypeThroughCatalogAndSim()
    {
        var file = new DeviceCatalogFile { Devices =
        {
            new DeviceProfile
            {
                DeviceId = "s7a",
                Protocol = "s7",
                Host = "127.0.0.1",
                Port = 102,
                PollIntervalMs = 100,
                Loopback = true,
                Tags =
                {
                    new TagProfile { Tag = "s7a/flag", Address = "db:0", DataType = "bool", Readable = true, Writable = true },
                    new TagProfile { Tag = "s7a/b1", Address = "db:1", DataType = "byte", Readable = true, Writable = true },
                    new TagProfile { Tag = "s7a/i16", Address = "db:2", DataType = "short", Readable = true, Writable = true },
                    new TagProfile { Tag = "s7a/u16", Address = "db:4", DataType = "ushort", Readable = true, Writable = true },
                    new TagProfile { Tag = "s7a/i32", Address = "db:6", DataType = "int", Readable = true, Writable = true },
                    new TagProfile { Tag = "s7a/u32", Address = "db:10", DataType = "uint", Readable = true, Writable = true },
                    new TagProfile { Tag = "s7a/f32", Address = "db:14", DataType = "float", Readable = true, Writable = true },
                    new TagProfile { Tag = "s7a/i64", Address = "db:18", DataType = "long", Readable = true, Writable = true },
                    new TagProfile { Tag = "s7a/u64", Address = "db:26", DataType = "ulong", Readable = true, Writable = true },
                    new TagProfile { Tag = "s7a/f64", Address = "db:34", DataType = "double", Readable = true, Writable = true },
                }
            }
        } };
        var sim = S7TcpSimulator.Start();
        await using var __ = sim;
        var catalog = new DeviceCatalog(file);
        var tags = catalog.BuildTagTable();
        var conn = catalog.CreateConnection(file.Devices[0], tags, loopbackPort: sim.Port);
        await using var ___ = conn;
        await conn.ConnectAsync(CancellationToken.None);

        await conn.WriteAsync("s7a/flag", true, CancellationToken.None);
        await conn.WriteAsync("s7a/b1", (byte)0x5A, CancellationToken.None);
        await conn.WriteAsync("s7a/i16", (short)-1234, CancellationToken.None);
        await conn.WriteAsync("s7a/u16", (ushort)0xBEEF, CancellationToken.None);
        await conn.WriteAsync("s7a/i32", -100_000, CancellationToken.None);
        await conn.WriteAsync("s7a/u32", 4_000_000_000u, CancellationToken.None);
        await conn.WriteAsync("s7a/f32", 3.25f, CancellationToken.None);
        await conn.WriteAsync("s7a/i64", -9_000_000_000L, CancellationToken.None);
        await conn.WriteAsync("s7a/u64", 18_000_000_000_000_000_000UL, CancellationToken.None);
        await conn.WriteAsync("s7a/f64", 3.141_592_653_589_793, CancellationToken.None);

        Assert.Equal(true, Convert.ToBoolean(await conn.ReadAsync("s7a/flag", CancellationToken.None)));
        Assert.Equal((byte)0x5A, Convert.ToByte(await conn.ReadAsync("s7a/b1", CancellationToken.None)));
        Assert.Equal((short)-1234, Convert.ToInt16(await conn.ReadAsync("s7a/i16", CancellationToken.None)));
        Assert.Equal((ushort)0xBEEF, Convert.ToUInt16(await conn.ReadAsync("s7a/u16", CancellationToken.None)));
        Assert.Equal(-100_000, Convert.ToInt32(await conn.ReadAsync("s7a/i32", CancellationToken.None)));
        Assert.Equal(4_000_000_000u, Convert.ToUInt32(await conn.ReadAsync("s7a/u32", CancellationToken.None)));
        Assert.Equal(3.25f, Convert.ToSingle(await conn.ReadAsync("s7a/f32", CancellationToken.None)));
        Assert.Equal(-9_000_000_000L, Convert.ToInt64(await conn.ReadAsync("s7a/i64", CancellationToken.None)));
        Assert.Equal(18_000_000_000_000_000_000UL, Convert.ToUInt64(await conn.ReadAsync("s7a/u64", CancellationToken.None)));
        Assert.Equal(3.141_592_653_589_793, Convert.ToDouble(await conn.ReadAsync("s7a/f64", CancellationToken.None)));
    }

    [Fact]
    public async Task RoundTrip_WriteThenRead_EndianAndWidth()
    {
        var sim = S7TcpSimulator.Start();
        await using var __ = sim;
        var (_, conn) = Build("dev", sim.Port);
        await using var ___ = conn;
        await conn.WriteAsync("dev/u16", (ushort)0x1234, CancellationToken.None);
        Assert.Equal((byte)0x12, sim.ReadDb(0));
        Assert.Equal((byte)0x34, sim.ReadDb(1));
        Assert.Equal((ushort)0x1234, Convert.ToUInt16(await conn.ReadAsync("dev/u16", CancellationToken.None)));

        sim.SeedDb(2, (byte)0xFF);
        sim.SeedDb(3, (byte)0xF8);
        sim.SeedDb(4, (byte)0x00);
        sim.SeedDb(5, (byte)0x01);
        Assert.Equal(new int[] { 0xFF, 0xF8, 0x00, 0x01 }, Enumerable.Range(0, 4).Select(i => (int)sim.ReadDb(2 + i)));
        Assert.Equal(unchecked((int)0xFFF80001), Convert.ToInt32(await conn.ReadAsync("dev/i32", CancellationToken.None)));
    }

    [Fact]
    public async Task RoundTrip_BoolAndLong_AtByteOffsets()
    {
        var sim = S7TcpSimulator.Start();
        await using var __ = sim;
        var (_, conn) = Build("dev", sim.Port);
        await using var ___ = conn;
        await conn.WriteAsync("dev/b", true, CancellationToken.None);
        Assert.Equal((byte)1, sim.ReadDb(18));
        await conn.WriteAsync("dev/h08", 0x0102_0304_0506_0708L, CancellationToken.None);
        Assert.Equal(new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08 },
            Enumerable.Range(0, 8).Select(i => sim.ReadDb(19 + i)).ToArray());
        Assert.Equal((long)0x0102_0304_0506_0708, Convert.ToInt64(await conn.ReadAsync("dev/h08", CancellationToken.None)));
    }

    [Fact]
    public async Task UnknownTag_And_StringType_ThrowClearErrors()
    {
        var sim = S7TcpSimulator.Start();
        await using var __ = sim;
        var (_, conn) = Build("dev", sim.Port);
        await using var ___ = conn;

        var unknown = await Assert.ThrowsAsync<ArgumentException>(() => conn.ReadAsync("dev/nope", CancellationToken.None));
        Assert.Contains("unknown tag", unknown.Message);

        var str = await Assert.ThrowsAsync<NotSupportedException>(() => conn.ReadAsync("dev/non", CancellationToken.None));
        Assert.Contains("String", str.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OutOfRange_DbAddress_AnswersS7Error()
    {
        var sim = S7TcpSimulator.Start();
        await using var __ = sim;
        var (_, conn) = Build("dev", sim.Port);
        await using var ___ = conn;
        var ex = await Assert.ThrowsAsync<S7Exception>(() => conn.ReadAsync("dev/r", CancellationToken.None));
        Assert.Equal(0x05, ex.ErrorCode);
    }

    [Fact]
    public async Task ConcurrentReads_AreSerialized()
    {
        var sim = S7TcpSimulator.Start();
        await using var __ = sim;
        var (_, conn) = Build("dev", sim.Port);
        await using var ___ = conn;
        await conn.WriteAsync("dev/u16", (ushort)7, CancellationToken.None);
        var tasks = Enumerable.Range(0, 20).Select(_ => conn.ReadAsync("dev/u16", CancellationToken.None)).ToArray();
        var results = await Task.WhenAll(tasks);
        Assert.All(results, r => Assert.Equal((ushort)7, Convert.ToUInt16(r)));
    }

    [Fact]
    public async Task Subscribe_PushesValuesOnChange()
    {
        var sim = S7TcpSimulator.Start();
        await using var __ = sim;
        var (_, conn) = Build("dev", sim.Port, poll: TimeSpan.FromMilliseconds(50));
        await using var ___ = conn;
        await conn.ConnectAsync(CancellationToken.None);

        var received = new List<TagValue>();
        using (conn.Subscribe("dev/*").Subscribe(new Collector(received)))
        {
            await conn.WriteAsync("dev/u16", (ushort)11, CancellationToken.None);
            await WaitUntilAsync(() => received.Any(v => Equals(v.Value, (ushort)11)), 3000);

            await conn.WriteAsync("dev/u16", (ushort)22, CancellationToken.None);
            await WaitUntilAsync(() => received.Any(v => Equals(v.Value, (ushort)22)), 3000);
        }
        Assert.True(received.Count >= 2, $"received {received.Count}");
        Assert.All(received, v => Assert.Equal(Quality.Good, v.Quality));
    }

    // ----- Reconnect / heartbeat · 重连 / 心跳 -----

    [Fact]
    public async Task ReactiveReconnect_AfterDropAll_HealsAndCounts()
    {
        var sim = S7TcpSimulator.Start();
        await using var __ = sim;
        var (_, conn) = Build("dev", sim.Port, reconnect: new ReconnectOptions
        {
            InitialDelayMs = 50, MaxDelayMs = 200, Multiplier = 2.0, MaxAttempts = 0
        });
        await using var ___ = conn;

        await conn.ConnectAsync(CancellationToken.None);
        await conn.WriteAsync("dev/u16", (ushort)1, CancellationToken.None);
        Assert.Equal(ConnectionState.Connected, conn.State);

        sim.DropAll();
        await Assert.ThrowsAnyAsync<Exception>(() => conn.WriteAsync("dev/u16", (ushort)2, CancellationToken.None));

        await WaitUntilAsync(() => conn.IsRecovering, 2000);
        Assert.Equal(ConnectionState.Reconnecting, conn.State);
        await WaitUntilAsync(() => conn.State == ConnectionState.Connected, 5000);
        Assert.False(conn.IsRecovering);
        Assert.True(conn.ReconnectCount >= 1, $"ReconnectCount={conn.ReconnectCount}");

        await conn.WriteAsync("dev/u16", (ushort)3, CancellationToken.None);
        Assert.Equal(ConnectionState.Connected, conn.State);
    }

    [Fact]
    public async Task Recovery_GivesUpInBoundedAttempts_WhenServerGone()
    {
        var sim = S7TcpSimulator.Start();
        var (_, conn) = Build("dev", sim.Port, reconnect: new ReconnectOptions
        {
            InitialDelayMs = 20, MaxDelayMs = 60, Multiplier = 1.3, MaxAttempts = 2
        });
        await using var ___ = conn;
        await conn.ConnectAsync(CancellationToken.None);
        await conn.WriteAsync("dev/u16", (ushort)1, CancellationToken.None);

        sim.DropAll();
        await sim.DisposeAsync();

        await Assert.ThrowsAnyAsync<Exception>(() => conn.WriteAsync("dev/u16", (ushort)2, CancellationToken.None));
        // FaultTransport also flips to Disconnected, so wait for the give-up, not just the state.
        // · 故障瞬间也会置为 Disconnected,须等到恢复循环真正放弃。
        await WaitUntilAsync(() => conn.State == ConnectionState.Disconnected && !conn.IsRecovering, 5000);
        Assert.False(conn.IsRecovering);
        Assert.Equal(0, conn.ReconnectCount);
        Assert.NotNull(conn.LastError);
    }

    [Fact]
    public async Task Heartbeat_DetectsSilentDrop_AndReconnects()
    {
        // Accepts + answers the connect handshake, then goes silent: the heartbeat probe times
        // out, faults the connection and drives recovery; each healed connection faults again
        // on the next probe, so ReconnectCount keeps growing.
        // / 接受连接并应答握手后静默:心跳探测超时→故障→恢复;每次愈合的连接在下一轮探测再次故障,
        //   ReconnectCount 持续增长。
        var slave = new BlackholeSlave();
        slave.Start();
        await using var __ = slave;
        var (_, conn) = Build("dev", slave.Port, reconnect: new ReconnectOptions
        {
            HeartbeatMs = 100, InitialDelayMs = 20, MaxDelayMs = 100, Multiplier = 1.5, MaxAttempts = 0
        });
        await using var ___ = conn;
        await conn.ConnectAsync(CancellationToken.None);
        Assert.Equal(ConnectionState.Connected, conn.State);
        Assert.Equal(0, conn.ReconnectCount);

        await WaitUntilAsync(() => conn.ReconnectCount >= 1, 8000);
    }
}