using System;
using System.Buffers.Binary;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Client;

public enum PacketType : byte
{
    Hello = 0x01,
    Welcome = 0x02,
    State = 0x10,
    CamChunk = 0x20,
    SaveChunk = 0x21,
}

public static class Proto
{
    public const ushort Version = 1;

    public static async Task ReadExactAsync(NetworkStream s, byte[] buf, int offset, int count, CancellationToken ct)
    {
        int readTotal = 0;
        while (readTotal < count)
        {
            int n = await s.ReadAsync(buf.AsMemory(offset + readTotal, count - readTotal), ct);
            if (n == 0)
            {
                throw new IOException("Server disconnected");
            }

            readTotal += n;
        }
    }

    public static async Task SendPacketAsync(NetworkStream s, PacketType type, byte[]? payload, CancellationToken ct)
    {
        int payloadLen = payload?.Length ?? 0;
        int bodyLen = 1 + payloadLen;
        if (bodyLen > ushort.MaxValue)
        {
            throw new InvalidOperationException("Packet too large");
        }

        byte[] header = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(header, (ushort)bodyLen);

        await s.WriteAsync(header, ct);
        await s.WriteAsync(new[] { (byte)type }, ct);
        if (payloadLen > 0)
        {
            await s.WriteAsync(payload!, ct);
        }
    }

    public static byte[] PackString(string? text)
    {
        text ??= "";
        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length > 255)
        {
            Array.Resize(ref bytes, 255);
        }

        var res = new byte[1 + bytes.Length];
        res[0] = (byte)bytes.Length;
        Buffer.BlockCopy(bytes, 0, res, 1, bytes.Length);
        return res;
    }

    public static string UnpackString(ReadOnlySpan<byte> span, ref int idx)
    {
        if (idx >= span.Length)
        {
            throw new InvalidDataException("Invalid string length");
        }

        byte len = span[idx++];
        if (idx + len > span.Length)
        {
            throw new InvalidDataException("Invalid string payload");
        }

        var s = Encoding.UTF8.GetString(span.Slice(idx, len));
        idx += len;
        return s;
    }
}
