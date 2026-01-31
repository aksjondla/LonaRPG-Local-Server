using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;

namespace Host;

public sealed class RubyBridgeUdp : IDisposable
{
    private readonly UdpClient _udp;
    private readonly IPEndPoint _to;

    public RubyBridgeUdp(int rubyPort)
    {
        _udp = new UdpClient();
        _to = new IPEndPoint(IPAddress.Loopback, rubyPort);
    }

    public void SendSnapshot(IReadOnlyCollection<PlayerState> players, TimeSpan staleTimeout)
    {
        if (players.Count == 0)
        {
            SendHeaderOnly();
            return;
        }

        int maxCount = Math.Min(players.Count, 255);
        byte[] buf = new byte[8 + maxCount * 16];

        buf[0] = (byte)'L';
        buf[1] = (byte)'C';
        buf[2] = (byte)'O';
        buf[3] = (byte)'1';
        buf[4] = 1;
        buf[5] = (byte)maxCount;
        buf[6] = 0;
        buf[7] = 0;

        int off = 8;
        int written = 0;
        var now = DateTime.UtcNow;

        foreach (var p in players)
        {
            if (written >= maxCount)
            {
                break;
            }

            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(off, 2), p.Pid);
            off += 2;
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(off, 2), p.Npc);
            off += 2;
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(off, 4), p.Seq);
            off += 4;

            ulong mask = (now - p.LastSeenUtc) > staleTimeout ? 0UL : p.KeysMask;
            uint lo = (uint)(mask & 0xFFFFFFFF);
            uint hi = (uint)(mask >> 32);

            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(off, 4), lo);
            off += 4;
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(off, 4), hi);
            off += 4;

            written++;
        }

        int totalLen = 8 + written * 16;
        if (totalLen <= 8)
        {
            SendHeaderOnly();
            return;
        }

        buf[5] = (byte)written;
        _udp.Send(buf, totalLen, _to);
    }

    public void Dispose()
    {
        _udp.Dispose();
    }

    private void SendHeaderOnly()
    {
        byte[] buf = new byte[8];
        buf[0] = (byte)'L';
        buf[1] = (byte)'C';
        buf[2] = (byte)'O';
        buf[3] = (byte)'1';
        buf[4] = 1;
        buf[5] = 0;
        buf[6] = 0;
        buf[7] = 0;
        _udp.Send(buf, buf.Length, _to);
    }
}
