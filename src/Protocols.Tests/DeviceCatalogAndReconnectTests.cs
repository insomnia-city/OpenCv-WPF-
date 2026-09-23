using System.Net;
using System.Net.Sockets;
using HalconWorkflow.Abstractions;
using HalconWorkflow.Protocols;
using HalconWorkflow.Protocols.Devices;
using HalconWorkflow.Protocols.Modbus;
using HalconWorkflow.Protocols.Serialization;
using Xunit;

namespace HalconWorkflow.Protocols.Tests;

/// <summary>
/// Device catalog serialization / round-trip tests (§7.1 stage-21). · 设备目录序列化/往返测试(§7.1 阶段21)
/// </summary>
public class DeviceCatalogTests
{
    [Fact]
    public void DemoCatalog_HasExpectedShape()
    {
        var file = DeviceCatalog.CreateDemoCatalog();
        var device = Assert.Single(file.Devices);
        Assert.Equal("demo", device.DeviceId);
        Assert.True(device.Loopback);
        Assert.True(device.HeartbeatMs > 0);
        Assert.Equal(5, device.Tags.Count);
        Assert.All(device.Tags, t => Assert.StartsWith("demo/", t.Tag, StringComparison.Ordinal));
    }

    [Fact]
    public void SerializeDeserialize_RoundTrips()
    {
        var file = DeviceCatalog.CreateDemoCatalog();
        var json = DeviceCatalogSerializer.Serialize(file);
        Assert.Contains("\"schema\"", json);
        Assert.Contains("demo/holding/count", json);

        var parsed = DeviceCatalogSerializer.Deserialize(json);
        Assert.Equal(file.Devices.Count, parsed.Devices.Count);
        var device = parsed.Devices[0];
        Assert.Equal("demo", device.DeviceId);
        Assert.Equal("modbus-tcp", device.Protocol);
        Assert.Equal(200, device.PollIntervalMs);
        Assert.Equal(200, device.HeartbeatMs);
        Assert.True(device.Loopback);
        Assert.Equal(5, device.Tags.Count);

        var tag = device.Tags.First(t => t.Tag == "demo/holding/count");
        Assert.Equal("holding:1", tag.Address);
        Assert.Equal("ushort", tag.DataType);
        Assert.True(tag.Writable);

        // Idempotent second round-trip. · 二次往返幂等
        var again = DeviceCatalogSerializer.Deserialize(DeviceCatalogSerializer.Serialize(parsed));
        Assert.Equal(DeviceCatalogSerializer.Serialize(parsed), DeviceCatalogSerializer.Serialize(again));
    }

    [Fact]
    public void Deserialize_DefaultFillsMissingFields()
    {
        var json = """
            { "schema": "halcon/device-catalog", "schemaVersion": 1, "version": 1,
              "devices": [ { "deviceId": "plc1", "protocol": "modbus-tcp" } ] }
            """;
        var file = DeviceCatalogSerializer.Deserialize(json);
        var d = Assert.Single(file.Devices);
        Assert.Equal("127.0.0.1", d.Host);
        Assert.Equal(502, d.Port);
        Assert.Equal((byte)1, d.UnitId);
        Assert.Equal(200, d.PollIntervalMs);
        Assert.False(d.Loopback);
        Assert.Empty(d.Tags);
    }

    [Fact]
    public void TryDeserialize_JunkOrWrongSchema_ReturnsNull()
    {
        Assert.Null(DeviceCatalogSerializer.TryDeserialize("not json"));
        Assert.Null(DeviceCatalogSerializer.TryDeserialize("""{ "schema": "other", "devices": [] }"""));
        var ok = DeviceCatalogSerializer.TryDeserialize(
            """{ "schema": "halcon/device-catalog", "devices": [] }""");
        Assert.NotNull(ok);
    }

    [Fact]
    public void TypeNameMapping_Canonical()
    {
        Assert.Equal(typeof(bool), DeviceTypeNames.ToType("bool"));
        Assert.Equal(typeof(ushort), DeviceTypeNames.ToType("holding::unknown"));
        Assert.Equal("double", DeviceTypeNames.FromType(typeof(double)));
        Assert.Equal("ushort", DeviceTypeNames.FromType(typeof(decimal)));
    }

    [Fact]
    public void TypeNameMapping_CaseInsensitive()
    {
        Assert.Equal(typeof(ushort), DeviceTypeNames.ToType("USHORT"));
    }

    [Fact]
    public void Catalog_BuildTagTable_RegistersAll()
    {
        var file = DeviceCatalog.CreateDemoCatalog();
        var catalog = new DeviceCatalog(file);
        Assert.True(catalog.RequiresLoopback);
        var tags = catalog.BuildTagTable();
        foreach (var t in file.Devices[0].Tags)
        {
            var entry = tags.Resolve(t.Tag);
            Assert.NotNull(entry);
            Assert.Equal(t.Address, entry!.ProtocolAddress);
            Assert.Equal(t.Readable, entry.Readable);
            Assert.Equal(t.Writable, entry.Writable);
        }
    }

    [Fact]
    public void Catalog_DuplicateDeviceOrTag_Throws()
    {
        var dup = new DeviceCatalogFile { Devices =
        {
            new DeviceProfile { DeviceId = "a" },
            new DeviceProfile { DeviceId = "a" }
        } };
        Assert.Throws<ArgumentException>(() => new DeviceCatalog(dup));

        var dupTag = new DeviceCatalogFile { Devices =
        {
            new DeviceProfile { DeviceId = "a", Tags =
            {
                new TagProfile { Tag = "a/x" }, new TagProfile { Tag = "a/X" }
            } }
        } };
        Assert.Throws<ArgumentException>(() => new DeviceCatalog(dupTag));
    }

    [Fact]
    public void Catalog_CreateConnection_UnknownProtocolThrows()
    {
        var catalog = new DeviceCatalog(new DeviceCatalogFile { Devices = { new DeviceProfile { DeviceId = "x", Protocol = "opc-ua" } } });
        var ex = Assert.Throws<NotSupportedException>(() =>
            catalog.CreateConnection(
                new DeviceProfile { DeviceId = "x", Protocol = "opc-ua" }, new TagTable()));
        Assert.Contains("opc-ua", ex.Message);
    }

    [Fact]
    public async Task Catalog_CreateConnection_ModbusRoundTripsWithRealSim()
    {
        var file = DeviceCatalog.CreateDemoCatalog();
        var catalog = new DeviceCatalog(file);
        var tags = catalog.BuildTagTable();
        var sim = ModbusTcpSimulator.Start();
        await using var __ = sim;
        var conn = catalog.CreateConnection(file.Devices[0], tags, loopbackPort: sim.Port);
        await using var ___ = conn;
        await conn.ConnectAsync(CancellationToken.None);
        await conn.WriteAsync("demo/holding/speed", (ushort)42, CancellationToken.None);
        var value = await conn.ReadAsync("demo/holding/speed", CancellationToken.None);
        Assert.Equal((ushort)42, Convert.ToUInt16(value));
    }
}

/// <summary>
/// Heartbeat + reconnect tests (§7.1 stage-21 heartbeat/reconnect). Uses a minimal in-process
/// Modbus "fake slave" that answers reads, so the healthy phase is a real round-trip, and can
/// RST a connection on demand (a deterministic cable pull).
/// · 心跳与重连测试(§7.1 阶段21)。进程内最小 Modbus 假从站应答读请求使"健康期"为真实往返,
///   并可在需要时以 RST 确定性拔线。
/// </summary>
public class ModbusReconnectTests
{
    private sealed class FakeSlave : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly object _gate = new();
        private readonly List<Socket> _accepted = [];
        private readonly CancellationTokenSource _cts = new();
        private readonly bool _answer;
        private Task? _loop;
        public int Port { get; }

        public FakeSlave(bool answer)
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _answer = answer;
        }

        public void Start() => _loop = AcceptLoopAsync();

        public void Halt()
        {
            try { _listener.Stop(); } catch { }
            _cts.Cancel();
        }

        /// <summary>RST every accepted connection (cable pull). · 对全部已接受连接发 RST(拔线)</summary>
        public void DropAll()
        {
            List<Socket> copies;
            lock (_gate) copies = [.. _accepted];
            foreach (var s in copies)
            {
                try
                {
                    s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Linger, new LingerOption(false, 0));
                    s.Close();
                }
                catch { }
            }
        }

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var socket = await _listener.AcceptSocketAsync(_cts.Token).ConfigureAwait(false);
                    lock (_gate) _accepted.Add(socket);
                    if (_answer) Serve(socket);
                    else _ = Task.Run(() => DrainQuietlyAsync(socket));
                }
            }
            catch (OperationCanceledException) { }
        }

        private async Task DrainQuietlyAsync(Socket s)
        {
            var buf = new byte[512];
            try { while (await s.ReceiveAsync(buf).ConfigureAwait(false) > 0) { } }
            catch { }
        }

        /// <summary>REPLIES holding/input reads with 0x0001 and echoes writes. · 应答 holding/input 读为 0x0001,写回显</summary>
        private void Serve(Socket s)
        {
            _ = Task.Run(async () =>
            {
                var buf = new byte[512];
                try
                {
                    while (!_cts.IsCancellationRequested)
                    {
                        int n = await s.ReceiveAsync(buf).ConfigureAwait(false);
                        if (n < 8) break;
                        byte tidHi = buf[0], tidLo = buf[1], unit = buf[6], fc = buf[7];
                        byte[] resp;
                        if (fc is 0x01 or 0x02 or 0x03 or 0x04)
                        {
                            resp = [tidHi, tidLo, 0, 0, 0, 5, unit, fc, 0x02, 0x00, 0x01];
                        }
                        else
                        {
                            resp = [tidHi, tidLo, 0, 0, 0, 4, unit, fc, buf[8], buf[9]];
                        }
                        await s.SendAsync(resp).ConfigureAwait(false);
                    }
                }
                catch { }
            });
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            Halt();
            lock (_gate)
            {
                foreach (var s in _accepted) { try { s.Dispose(); } catch { } }
                _accepted.Clear();
            }
            await Task.CompletedTask;
        }
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

    private static (TagTable Tags, ModbusTcpConnection Conn) Build(string prefix, FakeSlave server, ReconnectOptions? reconnect = null)
    {
        var tags = new TagTable();
        tags.Register(new($"{prefix}/h", prefix, "holding:0", typeof(ushort), true, true));
        var conn = new ModbusTcpConnection(prefix, "127.0.0.1", server.Port, 1, tags, reconnect: reconnect);
        return (tags, conn);
    }

    [Fact]
    public async Task ReactiveReconnect_AfterRst_HealsAndCounts()
    {
        var server = new FakeSlave(answer: true);
        server.Start();
        await using var srv = server;
        var (_, conn) = Build("dev", server, new ReconnectOptions
        {
            InitialDelayMs = 50, MaxDelayMs = 200, Multiplier = 2.0, MaxAttempts = 0
        });
        await using var c = conn;

        await conn.ConnectAsync(CancellationToken.None);
        await conn.WriteAsync("dev/h", (ushort)1, CancellationToken.None);
        Assert.Equal(ConnectionState.Connected, conn.State);
        Assert.False(conn.IsRecovering);

        // Cable pull: next write hits the RST → transport fault → auto recovery. · 拔线:下一次写触发 RST → 恢复
        server.DropAll();
        await Assert.ThrowsAnyAsync<Exception>(() => conn.WriteAsync("dev/h", (ushort)2, CancellationToken.None));

        await WaitUntilAsync(() => conn.IsRecovering, 2000);
        Assert.Equal(ConnectionState.Reconnecting, conn.State);

        await WaitUntilAsync(() => conn.State == ConnectionState.Connected, 5000);
        Assert.False(conn.IsRecovering);
        Assert.True(conn.ReconnectCount >= 1, $"ReconnectCount={conn.ReconnectCount}");

        // Fresh socket usable again. · 新套接字再次可用
        await conn.WriteAsync("dev/h", (ushort)3, CancellationToken.None);
        Assert.Equal(ConnectionState.Connected, conn.State);
    }

    [Fact]
    public async Task Recovery_GivesUpInBoundedAttempts_WhenServerGone()
    {
        var server = new FakeSlave(answer: true);
        server.Start();
        var (_, conn) = Build("dev", server, new ReconnectOptions
        {
            InitialDelayMs = 20, MaxDelayMs = 60, Multiplier = 1.3, MaxAttempts = 2
        });
        await using var c = conn;
        await conn.ConnectAsync(CancellationToken.None);
        await conn.WriteAsync("dev/h", (ushort)1, CancellationToken.None);

        server.DropAll();
        server.Halt();
        await server.DisposeAsync();

        await Assert.ThrowsAnyAsync<Exception>(() => conn.WriteAsync("dev/h", (ushort)2, CancellationToken.None));
        await WaitUntilAsync(() => conn.State == ConnectionState.Disconnected, 5000);
        Assert.False(conn.IsRecovering);
        Assert.Equal(0, conn.ReconnectCount);
        Assert.NotNull(conn.LastError);
    }

    [Fact]
    public async Task Heartbeat_DetectsSilentDrop_AndReconnects()
    {
        // Blackhole: accepts but never answers → heartbeat probe times out → fault → recover.
        // The reconnection itself succeeds (listener accepts), then the next probe faults again,
        // so ReconnectCount keeps growing. We only assert the loop is alive and self-healing.
        // / 黑洞:接受连接但不应答 → 心跳超时 → 故障→恢复。重连本身成功,下一轮探测再次故障,ReconnectCount 持续增长。
        var server = new FakeSlave(answer: false);
        server.Start();
        await using var srv = server;
        var (_, conn) = Build("dev", server, new ReconnectOptions
        {
            HeartbeatMs = 100, InitialDelayMs = 20, MaxDelayMs = 100, Multiplier = 1.5, MaxAttempts = 0
        });
        await using var c = conn;
        await conn.ConnectAsync(CancellationToken.None);
        Assert.Equal(ConnectionState.Connected, conn.State);
        Assert.False(conn.IsRecovering);

        // No poller subscribed → heartbeat is the sole liveness driver. · 无轮询订阅 → 心跳是唯一存活驱动
        Assert.Equal(0, conn.ReconnectCount);
        await WaitUntilAsync(() => conn.ReconnectCount >= 1, 8000);
    }
}