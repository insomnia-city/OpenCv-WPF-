using System.Buffers.Binary;

namespace HalconWorkflow.Protocols.S7;

/// <summary>
/// S7 protocol error: carries the function-level error code (response header Error
/// field) or the item return code that the device answered instead of 0xFF.
/// / S7 协议异常：携带功能级错误码(响应头 Error 字段)或设备应答的数据项返回码(非 0xFF)。
/// </summary>
public sealed class S7Exception : Exception
{
    /// <summary>Error/return code as answered by the device. / 设备应答的错误/返回码</summary>
    public int ErrorCode { get; }

    public S7Exception(int errorCode, string message)
        : base(message)
    {
        ErrorCode = errorCode;
    }
}

/// <summary>
/// Parsed S7 DB byte-range address from an adapter-local tag. Tag addresses look like
/// "db:0" (DB1 byte 0) or "db2:16" (DB2 byte 16). The tag's CLR data type decides how
/// many bytes the read/write transfers (1/2/4/8). Byte-offset addressing only (§13.1
/// stage-22 S7 P0; bits / I/Q/M areas and multi-var PDUs come with real-hardware P1).
/// / 由适配器本地地址解析出的 S7 DB 字节寻址。地址形如 "db:0"(DB1 字节0) 或
///   "db2:16"(DB2 字节16)。每次读写传输的字节数由 Tag 的 CLR 类型决定(1/2/4/8)。
///   本阶段仅支持字节偏移寻址(§13.1 阶段22 S7 P0;位/I/Q/M 区与多变量 PDU 留待真实硬件 P1)。
/// </summary>
public readonly record struct S7DbAddress(ushort DbNumber, int ByteOffset)
{
    public static S7DbAddress Parse(string address)
    {
        var sep = address.IndexOf(':');
        if (sep <= 0)
            throw new FormatException($"invalid s7 address '{address}' (expected db<N>:<byteOffset>)");
        var area = address[..sep];
        if (!area.StartsWith("db", StringComparison.OrdinalIgnoreCase))
            throw new FormatException($"invalid s7 area '{area}' (only db is supported this stage)");
        var num = area[2..];
        ushort db = num.Length == 0 ? (ushort)1
            : ushort.TryParse(num, out var d) && d >= 1 ? d
            : throw new FormatException($"invalid db number in '{address}'");
        if (!int.TryParse(address[(sep + 1)..], out var offset) || offset < 0)
            throw new FormatException($"invalid byte offset in '{address}'");
        return new S7DbAddress(db, offset);
    }

    /// <summary>Address bits used by the S7 item's 24-bit Address field. / 用于 S7 item 24 位 Address 字段的位地址</summary>
    public int BitAddress => ByteOffset * 8;
}

/// <summary>
/// Pure .NET S7 ISO-on-TCP framer (TPKT/COTP + S7 PDU, canonical S7comm layout; no
/// third-party stack). Builds connect / DB read / DB write telegrams and parses
/// complete job responses. The byte layout matches well-known S7comm stacks:
/// request header 10 bytes (protocol 0x32, type 0x01 job, seq, param len, data len),
/// response header 12 bytes (+ two Error bytes), parameters right after, data items
/// at 10+paramLen (responses: 12+paramLen). · 纯 .NET S7 ISO-on-TCP 组帧器
///   (TPKT/COTP + S7 PDU,正典 S7comm 布局,无第三方栈)。组装连接/DB 读/DB 写报文并解析
///   Job 应答。请求头 10 字节(协议 0x32、类型 0x01、序号、参数长、数据长),响应头 12 字节
///   (多两个 Error 字节),参数紧随其后,数据项位于 10+paramLen(响应为 12+paramLen)。
/// </summary>
internal static class S7Frame
{
    public const byte ProtocolId = 0x32;      // S7 protocol identifier · 协议标识
    public const byte Job = 0x01;             // job request · 作业请求
    public const byte AckData = 0x03;         // ack-data response · 应答数据
    public const byte FnRead = 0x04;          // read variable · 读变量
    public const byte FnWrite = 0x05;         // write variable · 写变量
    public const byte ItemSpec = 0x12;        // variable specification · 变量规格
    public const byte ItemSpecLen = 0x0A;     // length of rest of the item · item 其余长度
    public const byte SyntaxS7Any = 0x10;     // any-type addressing · any 型寻址
    public const byte TransportByte = 0x02;   // request item transport size: BYTE · 请求 item 传输尺寸 BYTE
    public const byte AreaDb = 0x84;          // DB memory area · 数据块区
    public const byte DataTransportByte = 0x04; // data-item transport size: BYTE/WORD/DWORD · 数据项传输尺寸(BYTE)
    public const byte ReturnCodeOk = 0xFF;    // item return code: success · 数据项返回码:成功
    public const ushort MaxItemBytes = 512;     // single DB byte range this stage supports · 本阶段单 DB 字节范围上限

    /// <summary>
    /// TPKT + COTP Connection Request (rack 0, slot 2; TSAP 0100/0102). This is the
    /// standard 22-byte connect telegram most S7 stacks send. · TPKT + COTP 连接请求
    ///   (机架0 槽位2;TSAP 0100/0102)。多数 S7 栈使用的标准 22 字节连接报文。
    /// </summary>
    public static byte[] BuildConnectRequest()
    {
        var f = new byte[22];
        f[0] = 0x03;                 // TPKT version · 版本
        f[1] = 0x00;
        f[2] = 0x00; f[3] = 0x16;    // TPKT total length = 22 · 总长
        f[4] = 0x11;                 // COTP header length = 17 · 头长
        f[5] = 0xE0;                 // Connection Request · 连接请求
        f[6] = 0x00; f[7] = 0x00;    // DST REF · 目的引用
        f[8] = 0x00; f[9] = 0x01;    // SRC REF · 源引用
        f[10] = 0x00;                // class · 类别
        f[11] = 0xC1; f[12] = 0x02; f[13] = 0x01; f[14] = 0x00;  // calling TSAP 0100 · 呼叫方
        f[15] = 0xC2; f[16] = 0x02; f[17] = 0x01; f[18] = 0x02;  // called TSAP 0102 · 被叫方
        f[19] = 0xC0; f[20] = 0x01; f[21] = 0x0A;                // max TPDU 1024 · 最大 TPDU
        return f;
    }

    /// <summary>
    /// Reads and validates the COTP Connection Confirm response to the connect request.
    /// / 读取并校验连接请求的 COTP 连接确认(CC)响应。
    /// </summary>
    public static async Task ReadConnectConfirmAsync(Stream stream, CancellationToken ct)
    {
        var head = new byte[4];
        await stream.ReadExactlyAsync(head, ct).ConfigureAwait(false);
        if (head[0] != 0x03 || head[1] != 0x00)
            throw new InvalidDataException("s7: bad TPKT in connect confirm");
        int total = (head[2] << 8) | head[3];
        if (total < 4)
            throw new InvalidDataException("s7: TPKT too short in connect confirm");
        var body = new byte[total - 4];
        await stream.ReadExactlyAsync(body, ct).ConfigureAwait(false);
        if (body.Length < 2 || body[1] != 0xD0)
            throw new InvalidDataException("s7: connection refused (expected COTP 0xD0 confirm)");
    }

    /// <summary>Builds a DB byte-range read telegram. / 组装 DB 字节区间读报文</summary>
    public static byte[] BuildRead(ushort seq, S7DbAddress addr, int byteCount)
    {
        if (byteCount is < 1 or > MaxItemBytes)
            throw new ArgumentOutOfRangeException(nameof(byteCount), "s7 read range must be 1..512 bytes");
        var pdu = new byte[10 + 14];
        SetRequestHeader(pdu, seq, paramLen: 14, dataLen: 0);
        pdu[10] = FnRead;
        pdu[11] = 0x01; // item count · 条目数
        BuildItem(pdu.AsSpan(12), addr, byteCount);
        return Telegram(pdu);
    }

    /// <summary>Builds a DB byte-range write telegram. / 组装 DB 字节区间写报文</summary>
    public static byte[] BuildWrite(ushort seq, S7DbAddress addr, ReadOnlySpan<byte> data)
    {
        if (data.Length is < 1 or > MaxItemBytes)
            throw new ArgumentOutOfRangeException(nameof(data), "s7 write range must be 1..512 bytes");
        var pdu = new byte[10 + 14 + 4 + data.Length];
        SetRequestHeader(pdu, seq, paramLen: 14, dataLen: 4 + data.Length);
        pdu[10] = FnWrite;
        pdu[11] = 0x01;
        BuildItem(pdu.AsSpan(12), addr, data.Length);
        int ds = 10 + 14; // data section · 数据段
        pdu[ds] = 0x00;                       // return code on a write request is zero · 写请求返回码为 0
        pdu[ds + 1] = DataTransportByte;      // transport size · 传输尺寸
        int bits = data.Length * 8;
        pdu[ds + 2] = (byte)(bits >> 8);
        pdu[ds + 3] = (byte)bits;
        data.CopyTo(pdu.AsSpan(ds + 4));
        return Telegram(pdu);
    }

    /// <summary>
    /// Reads one complete job response (TPKT + COTP DT) and returns the raw S7 PDU
    /// (response header 12 bytes + parameter + data). · 读取一整个作业响应(TPKT+COTP DT)
    ///   并返回原始 S7 PDU(12 字节响应头+参数+数据)。
    /// </summary>
    public static async Task<byte[]> ReadPduAsync(Stream stream, CancellationToken ct)
    {
        var head = new byte[4];
        await stream.ReadExactlyAsync(head, ct).ConfigureAwait(false);
        if (head[0] != 0x03 || head[1] != 0x00)
            throw new InvalidDataException("s7: bad TPKT header in response");
        int total = (head[2] << 8) | head[3];
        if (total < 7)
            throw new InvalidDataException("s7: TPKT too short for a data telegram");
        var body = new byte[total - 4];
        await stream.ReadExactlyAsync(body, ct).ConfigureAwait(false);
        if (body.Length < 3 || body[0] != 0x02 || body[1] != 0xF0)
            throw new InvalidDataException("s7: missing COTP DT telegram in response");
        return body[3..];
    }

    /// <summary>
    /// Extracts the payload bytes of a read response, validating the header, the echoed
    /// sequence, the function-level error and the item return code. · 提取读响应负载,
    ///   校验响应头/序号回显/功能级错误与数据项返回码。
    /// </summary>
    public static byte[] ExtractReadResult(byte[] pdu, ushort expectedSeq)
    {
        int dataStart = CheckResponse(pdu, expectedSeq);
        if (pdu.Length < dataStart + 1)
            throw new InvalidDataException("s7: truncated read response");
        byte rc = pdu[dataStart];
        if (rc != ReturnCodeOk)
            throw new S7Exception(rc, $"s7 read answered item error 0x{rc:X2}");
        if (pdu.Length < dataStart + 4)
            throw new InvalidDataException("s7: truncated read response");
        byte transport = pdu[dataStart + 1];
        int bitLength = (pdu[dataStart + 2] << 8) | pdu[dataStart + 3];
        // 0x00 (null) and 0x09 (octet string) carry a byte length; everything else bits.
        // 0x00(null) 与 0x09(八位组串) 长度按字节,其余按位。
        int n = transport is 0x00 or 0x09 ? bitLength : bitLength / 8;
        if (n < 0 || dataStart + 4 + n > pdu.Length)
            throw new InvalidDataException("s7: read data length out of range");
        return pdu.AsSpan(dataStart + 4, n).ToArray();
    }

    /// <summary>
    /// Validates a write response (echoed seq, no function error, data item 0xFF).
    /// / 校验写应答(序号回显、无功能级错误、数据项返回 0xFF)。
    /// </summary>
    public static void VerifyWriteAck(byte[] pdu, ushort expectedSeq)
    {
        int dataStart = CheckResponse(pdu, expectedSeq);
        if (pdu.Length < dataStart + 1)
            throw new InvalidDataException("s7: truncated write response");
        if (pdu[dataStart] != ReturnCodeOk)
            throw new S7Exception(pdu[dataStart], $"s7 write answered item error 0x{pdu[dataStart]:X2}");
    }

    /// <summary>Validates the shared response header; returns the data section offset. / 校验共同响应头;返回数据段偏移</summary>
    private static int CheckResponse(byte[] pdu, ushort expectedSeq)
    {
        if (pdu.Length < 12)
            throw new InvalidDataException("s7: response too short");
        if (pdu[0] != ProtocolId || pdu[1] != AckData)
            throw new InvalidDataException("s7: bad response header");
        ushort seq = (ushort)((pdu[4] << 8) | pdu[5]);
        if (seq != expectedSeq)
            throw new InvalidDataException($"s7: sequence mismatch (sent {expectedSeq}, got {seq})");
        int paramLen = (pdu[6] << 8) | pdu[7];
        int error = (pdu[10] << 8) | pdu[11];
        if (error != 0)
            throw new S7Exception(error, $"s7 function error 0x{error:X4}");
        if (paramLen != 2)
            throw new InvalidDataException($"s7: unexpected parameter length {paramLen}");
        return 12 + paramLen; // response: 12-byte header + 2-byte param (fn + count) · 响应: 12 字节头 + 2 字节参数
    }

    private static void SetRequestHeader(Span<byte> pdu, ushort seq, int paramLen, int dataLen)
    {
        pdu[0] = ProtocolId;           // 0x32
        pdu[1] = Job;                  // 0x01 job request
        pdu[2] = 0; pdu[3] = 0;        // reserved (AB_EX)
        pdu[4] = (byte)(seq >> 8); pdu[5] = (byte)seq;
        pdu[6] = (byte)(paramLen >> 8); pdu[7] = (byte)paramLen;
        pdu[8] = (byte)(dataLen >> 8); pdu[9] = (byte)dataLen;
    }

    private static void BuildItem(Span<byte> item, S7DbAddress addr, int byteCount)
    {
        item[0] = ItemSpec;            // 0x12 variable specification
        item[1] = ItemSpecLen;         // 0x0A length of the rest
        item[2] = SyntaxS7Any;         // 0x10 any-type addressing
        item[3] = TransportByte;       // 0x02 BYTE transport
        item[4] = (byte)(byteCount >> 8);
        item[5] = (byte)byteCount;     // element count (= bytes for BYTE) · 元素数(字节型即字节数)
        item[6] = (byte)(addr.DbNumber >> 8);
        item[7] = (byte)addr.DbNumber;
        item[8] = AreaDb;              // 0x84 DB memory area
        int bits = addr.BitAddress;
        item[9] = (byte)((bits >> 16) & 0xFF);
        item[10] = (byte)((bits >> 8) & 0xFF);
        item[11] = (byte)(bits & 0xFF); // 24-bit bit address · 24 位位地址
    }

    /// <summary>Wraps an S7 PDU into TPKT + COTP DT telegram. / 把 S7 PDU 包进 TPKT+COTP DT 报文</summary>
    private static byte[] Telegram(ReadOnlySpan<byte> pdu)
    {
        int total = 7 + pdu.Length;
        var frame = new byte[total];
        frame[0] = 0x03; frame[1] = 0x00;
        frame[2] = (byte)(total >> 8); frame[3] = (byte)total;
        frame[4] = 0x02; frame[5] = 0xF0; frame[6] = 0x80; // COTP DT EOT · 数据电报
        pdu.CopyTo(frame.AsSpan(7));
        return frame;
    }
}