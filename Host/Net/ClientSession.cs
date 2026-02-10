using System;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Host;

public sealed class ClientSession
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly TcpHostServer _server;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public IPEndPoint RemoteEndPoint { get; }
    public ushort AssignedPid { get; private set; }
    public bool HandshakeDone { get; private set; }

    public ClientSession(TcpClient client, TcpHostServer server)
    {
        _client = client;
        _stream = client.GetStream();
        _server = server;

        _client.NoDelay = true;
        RemoteEndPoint = (IPEndPoint)_client.Client.RemoteEndPoint!;
    }

    public void RequestClose()
    {
        try
        {
            _client.Close();
        }
        catch
        {
        }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await ReadLoopAsync(ct);
        }
        catch (Exception)
        {
            // swallow to keep server alive
        }
        finally
        {
            _server.OnClientDisconnected(this);
            _client.Close();
        }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var lenBuf = new byte[2];

        while (!ct.IsCancellationRequested)
        {
            await Proto.ReadExactAsync(_stream, lenBuf, 0, 2, ct);
            ushort bodyLen = (ushort)(lenBuf[0] | (lenBuf[1] << 8));
            if (bodyLen < 1)
            {
                return;
            }

            byte[] body = new byte[bodyLen];
            await Proto.ReadExactAsync(_stream, body, 0, bodyLen, ct);

            var type = (PacketType)body[0];
            int payloadOffset = 1;
            int payloadLength = bodyLen - 1;

            switch (type)
            {
                case PacketType.Hello:
                    await HandleHelloAsync(body, payloadOffset, payloadLength, ct);
                    break;

                case PacketType.State:
                    if (HandshakeDone)
                    {
                        _server.HandleState(this, body, payloadOffset, payloadLength);
                    }

                    break;

                default:
                    return;
            }
        }
    }

    private async Task HandleHelloAsync(byte[] body, int offset, int length, CancellationToken ct)
    {
        int idx = offset;
        int end = offset + length;
        if (length < 2 + 2 + 1)
        {
            return;
        }

        ushort ver = ReadUInt16LE(body, ref idx);
        ushort desiredPid = ReadUInt16LE(body, ref idx);
        if (idx >= end)
        {
            return;
        }

        byte flags = body[idx++];

        string? name = null;
        if ((flags & 1) != 0)
        {
            if (idx >= end)
            {
                return;
            }

            int nameLen = body[idx++];
            if (idx + nameLen > end)
            {
                return;
            }

            name = Encoding.UTF8.GetString(body, idx, nameLen);
            idx += nameLen;
        }

        if (ver != Proto.Version)
        {
            var msg = Proto.PackString($"Protocol mismatch: server={Proto.Version}, client={ver}");
            await SendPacketAsync(PacketType.Welcome, BuildWelcomePayload(Proto.Version, 0, ok: false, msg), ct);
            _client.Close();
            return;
        }

        AssignedPid = _server.AssignPid(desiredPid);
        HandshakeDone = true;

        _server.RegisterPlayer(AssignedPid, name);

        await SendPacketAsync(PacketType.Welcome,
            BuildWelcomePayload(Proto.Version, AssignedPid, ok: true, Proto.PackString("ok")),
            ct);
    }

    public async Task<bool> TrySendAsync(PacketType type, byte[] payload, CancellationToken ct, bool dropIfBusy)
    {
        bool acquired = false;
        try
        {
            if (dropIfBusy)
            {
                acquired = await _sendLock.WaitAsync(0, ct);
                if (!acquired)
                {
                    return false;
                }
            }
            else
            {
                await _sendLock.WaitAsync(ct);
                acquired = true;
            }

            await Proto.SendPacketAsync(_stream, type, payload, ct);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch
        {
            try
            {
                _client.Close();
            }
            catch
            {
            }

            return false;
        }
        finally
        {
            if (acquired)
            {
                try
                {
                    _sendLock.Release();
                }
                catch
                {
                }
            }
        }
    }

    public Task SendPacketAsync(PacketType type, byte[] payload, CancellationToken ct)
        => TrySendAsync(type, payload, ct, dropIfBusy: false);

    public async Task<bool> TrySendCamFrameAsync(uint camId, byte[] frame, int maxChunkBytes, CancellationToken ct)
    {
        if (frame.Length == 0)
        {
            return true;
        }

        if (maxChunkBytes < 1)
        {
            maxChunkBytes = 1;
        }

        bool acquired = false;
        try
        {
            acquired = await _sendLock.WaitAsync(0, ct);
            if (!acquired)
            {
                return false;
            }

            uint totalLen = (uint)frame.Length;
            int offset = 0;
            while (offset < frame.Length)
            {
                int chunkLen = Math.Min(maxChunkBytes, frame.Length - offset);
                var payload = new byte[12 + chunkLen];

                BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), camId);
                BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4, 4), totalLen);
                BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8, 4), (uint)offset);
                Buffer.BlockCopy(frame, offset, payload, 12, chunkLen);

                await Proto.SendPacketAsync(_stream, PacketType.CamChunk, payload, ct);
                offset += chunkLen;
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch
        {
            try
            {
                _client.Close();
            }
            catch
            {
            }

            return false;
        }
        finally
        {
            if (acquired)
            {
                try
                {
                    _sendLock.Release();
                }
                catch
                {
                }
            }
        }
    }

    private static byte[] BuildWelcomePayload(ushort ver, ushort pid, bool ok, byte[] msgPacked)
    {
        byte[] res = new byte[2 + 2 + 1 + msgPacked.Length];
        int i = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(res.AsSpan(i, 2), ver);
        i += 2;
        BinaryPrimitives.WriteUInt16LittleEndian(res.AsSpan(i, 2), pid);
        i += 2;
        res[i++] = ok ? (byte)1 : (byte)0;
        Buffer.BlockCopy(msgPacked, 0, res, i, msgPacked.Length);
        return res;
    }

    private static ushort ReadUInt16LE(byte[] buffer, ref int idx)
    {
        if (idx + 1 >= buffer.Length)
        {
            return 0;
        }

        ushort value = (ushort)(buffer[idx] | (buffer[idx + 1] << 8));
        idx += 2;
        return value;
    }
}
