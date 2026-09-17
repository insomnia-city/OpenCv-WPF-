namespace HalconWorkflow.Protocols.Modbus;

/// <summary>
/// Modbus TCP exception, carries the Modbus exception code (§7.3 ADU framing). 
/// / Modbus TCP 异常，携带 Modbus 异常码（§7.3 ADU 帧）。
/// </summary>
public sealed class ModbusException : Exception
{
    public byte FunctionCode { get; }
    public byte ExceptionCode { get; }

    public ModbusException(byte functionCode, byte exceptionCode)
        : base($"Modbus error 0x{exceptionCode:X2} for function 0x{functionCode:X2}")
    {
        FunctionCode = functionCode;
        ExceptionCode = exceptionCode;
    }
}

/// <summary>
/// Register area for a Modbus tag address. Tag addresses look like
/// "coil:0", "discrete:0", "holding:0", "input:0". / 标签地址所属寄存器区。
///   地址形如 "coil:0"、"discrete:0"、"holding:0"、"input:0"。
/// </summary>
public enum ModbusArea
{
    Coil,        // FC1 read / FC5 write single coil · 线圈
    Discrete,    // FC2 read only · 离散输入
    Holding,     // FC3 read / FC6 write single register · 保持寄存器
    Input,       // FC4 read only · 输入寄存器
}

/// <summary>
/// Parsed Modbus register address from an adapter-local tag. / 由适配器本地地址解析出的寄存器地址
/// </summary>
public readonly record struct ModbusRegisterAddress(ModbusArea Area, int Index)
{
    public static ModbusRegisterAddress Parse(string address)
    {
        var sep = address.IndexOf(':');
        if (sep <= 0)
            throw new FormatException($"invalid modbus address '{address}' (expected area:index)");
        var area = Enum.Parse<ModbusArea>(address[..sep], ignoreCase: true);
        if (!int.TryParse(address[(sep + 1)..], out var index) || index < 0)
            throw new FormatException($"invalid modbus index in '{address}'");
        return new ModbusRegisterAddress(area, index);
    }

    /// <summary>Function code read variant for this area. / 该区的读功能码</summary>
    public byte ReadFunctionCode => Area switch
    {
        ModbusArea.Coil => 0x01,
        ModbusArea.Discrete => 0x02,
        ModbusArea.Holding => 0x03,
        _ => 0x04,
    };

    /// <summary>Function code write variant; null when read-only. / 该区的写功能码;只读区为 null</summary>
    public byte? WriteFunctionCode => Area switch
    {
        ModbusArea.Coil => 0x05,
        ModbusArea.Holding => 0x06,
        _ => null,
    };
}

/// <summary>
/// Pure .NET MBAP framer (no third-party Modbus stack): builds MBAP+PDU requests and
/// reads complete ADU responses off the stream. · / 纯 .NET MBAP 组帧器(不引第三方栈)：
///   组装 MBAP+PDU 请求并从流上读取完整 ADU 响应。
/// </summary>
internal static class ModbusFrame
{
    /// <summary>Reads one full Modbus TCP ADU frame; returns the raw PDU bytes. / 读取一整帧 ADU;返回 PDU 字节</summary>
    public static async Task<byte[]> ReadAduPduAsync(Stream stream, ushort transactionId, byte unitId, CancellationToken ct)
    {
        var header = new byte[7];
        await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        var tid = (ushort)((header[0] << 8) | header[1]);
        var pid = (ushort)((header[2] << 8) | header[3]);
        var length = (ushort)((header[4] << 8) | header[5]);
        var respUnit = header[6];
        if (pid != 0 || tid != transactionId || respUnit != unitId)
            throw new InvalidDataException("modbus ADU header mismatch (tid/pid/unit)");
        if (length < 1)
            throw new InvalidDataException("modbus ADU has empty PDU");
        var pdu = new byte[length - 1];
        await stream.ReadExactlyAsync(pdu, ct).ConfigureAwait(false);
        return pdu;
    }

    /// <summary>
    /// Builds the full request ADU for a one-item read or write.
    /// / 组装单项读/写请求的完整 ADU。
    /// </summary>
    public static byte[] BuildRequest(ushort transactionId, byte unitId, params byte[] pdu)
    {
        var frame = new byte[7 + pdu.Length];
        frame[0] = (byte)(transactionId >> 8);
        frame[1] = (byte)transactionId;
        frame[2] = 0; // protocol id · 协议ID
        frame[3] = 0;
        frame[4] = (byte)((pdu.Length + 1) >> 8);
        frame[5] = (byte)(pdu.Length + 1);
        frame[6] = unitId;
        pdu.CopyTo(frame, 7);
        return frame;
    }
}