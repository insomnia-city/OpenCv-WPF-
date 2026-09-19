namespace HalconWorkflow.Protocols.Modbus;

/// <summary>
/// Heartbeat + reconnect tuning for a <see cref="ModbusTcpConnection"/> (§7.1 stage-21).
/// Defaults are off / unlimited retry: the adapter only auto-reconnects after a transport
/// failure, with exponential backoff capped at <see cref="MaxDelayMs"/>.
/// · ModbusTcpConnection 的心跳与重连参数(§7.1 阶段21)。默认不探活、无限重试:
///   仅在传输故障后自动重连,按 MaxDelayMs 封顶的指数退避。
/// </summary>
public sealed class ReconnectOptions
{
    /// <summary>
    /// Proactive heartbeat cadence in ms; 0 = off. When no change-poller is subscribed the
    /// adapter probes the first readable tag to detect silent drops and steer recovery.
    /// · 主动心跳周期(ms);0 关闭。无轮询订阅时探测首个可读 Tag 以发现静默断连并驱动恢复。
    /// </summary>
    public int HeartbeatMs { get; set; } = 0;

    /// <summary>Backoff after the first failed attempt, ms. · 首次失败后的退避时长(ms)</summary>
    public int InitialDelayMs { get; set; } = 200;

    /// <summary>Maximum backoff between attempts, ms. · 尝试之间的最大退避(ms)</summary>
    public int MaxDelayMs { get; set; } = 5000;

    /// <summary>Backoff growth per attempt (>=1.0). · 每次失败的退避倍数(>=1.0)</summary>
    public double Multiplier { get; set; } = 2.0;

    /// <summary>
    /// Reconnect tries per recovery session before giving up; 0 = unlimited. After a successful
    /// reconnect a new session begins on the next failure.
    /// · 单次恢复会话的最大重连尝试数;0 = 不限。成功后,再次故障时开启新一轮会话。
    /// </summary>
    public int MaxAttempts { get; set; } = 0;

    /// <summary>Keeps a recovery session bounded even for a blackhole peer. · 黑洞对端时也要让单次连接尝试有界</summary>
    public int ConnectTimeoutMs { get; set; } = 2000;
}