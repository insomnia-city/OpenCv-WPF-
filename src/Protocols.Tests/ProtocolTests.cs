using HalconWorkflow.Abstractions;
using HalconWorkflow.Protocols;
using HalconWorkflow.Protocols.Modbus;
using Xunit;

namespace HalconWorkflow.Protocols.Tests;

/// <summary>
/// Tag table protocol-agnostic I/O tests (§13.1 Stage-6 gate · 换协议不动图).
/// / 协议无关 Tag 表 I/O 测试（§13.1 阶段6 门 · 换协议不动图）
/// </summary>
public class TagTableTests
{
    [Fact]
    public void RegisterResolve_RoundTrip()
    {
        var tags = new TagTable();
        tags.Register(new("plc1/holding/speed", "plc1", "holding:0", typeof(ushort), true, true));
        var entry = tags.Resolve("plc1/holding/speed");
        Assert.NotNull(entry);
        Assert.Equal("holding:0", entry!.ProtocolAddress);
        Assert.Equal("plc1", entry.DeviceId);
    }

    [Fact]
    public void UnknownTag_ReturnsNull()
    {
        var tags = new TagTable();
        Assert.Null(tags.Resolve("unknown"));
    }

    [Fact]
    public void All_ReturnsSnapshot()
    {
        var tags = new TagTable();
        tags.Register(new("a", "dev", "holding:0", typeof(int), true, true));
        tags.Register(new("b", "dev", "holding:1", typeof(int), true, true));
        var all = tags.All().ToList();
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public void ConcurrentRegisterResolve_NoException()
    {
        var tags = new TagTable();
        Parallel.For(0, 200, i =>
        {
            tags.Register(new($"dev/coils/{i}", "dev", $"coil:{i}", typeof(bool), true, true));
            Assert.NotNull(tags.Resolve($"dev/coils/{i}"));
        });
    }

    [Fact]
    public void ConcurrentTagTable_AfterAllRegistered_AllResolvable()
    {
        var tags = new TagTable();
        Parallel.For(0, 500, i =>
            tags.Register(new($"dev/{i}", "dev", $"holding:{i}", typeof(ushort), true, true)));
        for (int i = 0; i < 500; i++)
            Assert.NotNull(tags.Resolve($"dev/{i}"));
    }
}

/// <summary>
/// Modbus TCP simulator loopback round-trip tests: real TCP framing against an in-process slave. 
/// / Modbus TCP 模拟器回环往返测试：真实 TCP 帧对进程内从站
/// </summary>
public class ModbusTcpRoundTripTests
{
    [Fact]
    public async Task WriteReadHolding_RoundTrips()
    {
        var tags = new TagTable();
        tags.Register(new("dev/holding/speed", "dev", "holding:0", typeof(ushort), true, true));
        var sim = ModbusTcpSimulator.Start();
        await using var __ = sim;
        var conn = new ModbusTcpConnection("dev", "127.0.0.1", sim.Port, 0xFF, tags);
        await using var ___ = conn;
        await conn.ConnectAsync(CancellationToken.None);
        await conn.WriteAsync("dev/holding/speed", (ushort)1234, CancellationToken.None);
        var value = await conn.ReadAsync("dev/holding/speed", CancellationToken.None);
        Assert.Equal((ushort)1234, Convert.ToUInt16(value));
        Assert.Equal((ushort)1234, sim.ReadHolding(0));
    }

    [Fact]
    public async Task WriteReadCoil_RoundTrips()
    {
        var tags = new TagTable();
        tags.Register(new("dev/coils/start", "dev", "coil:0", typeof(bool), true, true));
        var sim = ModbusTcpSimulator.Start();
        await using var __ = sim;
        var conn = new ModbusTcpConnection("dev", "127.0.0.1", sim.Port, 0xFF, tags);
        await using var ___ = conn;
        await conn.ConnectAsync(CancellationToken.None);
        await conn.WriteAsync("dev/coils/start", true, CancellationToken.None);
        var value = await conn.ReadAsync("dev/coils/start", CancellationToken.None);
        Assert.True(Convert.ToBoolean(value));
        Assert.True(sim.ReadCoil(0));
    }

    [Fact]
    public async Task UnknownTag_Faults()
    {
        var tags = new TagTable();
        tags.Register(new("dev/holding/x", "dev", "holding:0", typeof(ushort), true, true));
        var sim = ModbusTcpSimulator.Start();
        await using var __ = sim;
        var conn = new ModbusTcpConnection("dev", "127.0.0.1", sim.Port, 0xFF, tags);
        await using var ___ = conn;
        await Assert.ThrowsAsync<ArgumentException>(() => conn.ReadAsync("no", CancellationToken.None));
    }

    [Fact]
    public async Task OutOfRangeAddress_ModbusException_0x02()
    {
        var tags = new TagTable();
        tags.Register(new("dev/holding/far", "dev", "holding:9999", typeof(ushort), true, true));
        var sim = ModbusTcpSimulator.Start();
        await using var __ = sim;
        var conn = new ModbusTcpConnection("dev", "127.0.0.1", sim.Port, 0xFF, tags);
        await using var ___ = conn;
        var ex = await Assert.ThrowsAsync<ModbusException>(() =>
            conn.ReadAsync("dev/holding/far", CancellationToken.None));
        Assert.Equal(0x02, ex.ExceptionCode);
    }

    [Fact]
    public async Task ParallelReadWrite_NoInterleave()
    {
        var tags = new TagTable();
        for (int i = 0; i < 10; i++)
            tags.Register(new($"dev/h{i}", "dev", $"holding:{i}", typeof(ushort), true, true));
        var sim = ModbusTcpSimulator.Start();
        await using var __ = sim;
        var conn = new ModbusTcpConnection("dev", "127.0.0.1", sim.Port, 0xFF, tags);
        await using var ___ = conn;
        await conn.ConnectAsync(CancellationToken.None);
        var tasks = Enumerable.Range(0, 10).Select(i =>
            Task.Run(async () =>
            {
                ushort v = (ushort)(i * 100);
                await conn.WriteAsync($"dev/h{i}", v, CancellationToken.None);
                var read = await conn.ReadAsync($"dev/h{i}", CancellationToken.None);
                Assert.Equal(v, Convert.ToUInt16(read));
            }));
        await Task.WhenAll(tasks);
    }
}

/// <summary>
/// Matching helper: simple unit test for the subscription wildcard matcher. 
/// / 匹配辅助函数：通配符匹配单元测试
/// </summary>
public class ModbusMatcherTests
{
    [Fact]
    public void Matches_WildcardAndExact()
    {
        Assert.True(ModbusTcpConnection.Matches("*", "any"));
        Assert.True(ModbusTcpConnection.Matches("dev/*", "dev/x"));
        Assert.False(ModbusTcpConnection.Matches("dev/*", "other/x"));
        Assert.True(ModbusTcpConnection.Matches("exact", "exact"));
        Assert.False(ModbusTcpConnection.Matches("exact", "nope"));
    }
}