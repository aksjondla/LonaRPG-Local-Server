using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Host;

public sealed class TcpHostServer
{
    private readonly TcpListener _listener;
    private readonly ConcurrentDictionary<ushort, PlayerState> _players = new();
    private readonly ConcurrentDictionary<ClientSession, byte> _sessions = new();
    private ushort _nextPid = 1;
    private uint _nextCamId = 1;

    private const int MaxChunkBytes = 60000;

    public TcpHostServer(IPAddress bindIp, int port)
    {
        _listener = new TcpListener(bindIp, port);
    }

    public void Start() => _listener.Start();

    public void Stop() => _listener.Stop();

    public async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            var session = new ClientSession(client, this);
            _sessions.TryAdd(session, 0);
            _ = Task.Run(() => session.RunAsync(ct), ct);
        }
    }

    public ushort AssignPid(ushort desired)
    {
        if (desired != 0 && _players.TryAdd(desired, new PlayerState { Pid = desired }))
        {
            return desired;
        }

        while (true)
        {
            ushort pid = _nextPid++;
            if (pid == 0)
            {
                pid = _nextPid++;
            }

            if (_players.TryAdd(pid, new PlayerState { Pid = pid }))
            {
                return pid;
            }
        }
    }

    public void RegisterPlayer(ushort pid, string? name)
    {
        var st = _players.GetOrAdd(pid, _ => new PlayerState { Pid = pid });
        st.Name = name;
        st.LastSeenUtc = DateTime.UtcNow;
    }

    public void HandleState(ClientSession session, byte[] body, int offset, int length)
    {
        if (length < 2 + 2 + 4 + 8)
        {
            return;
        }

        int i = offset;
        int end = offset + length;
        if (i + 1 >= end)
        {
            return;
        }

        ushort pid = ReadUInt16LE(body, ref i);

        if (pid != session.AssignedPid)
        {
            return;
        }

        if (i + 1 >= end)
        {
            return;
        }

        ushort npc = ReadUInt16LE(body, ref i);
        if (i + 3 >= end)
        {
            return;
        }

        uint seq = ReadUInt32LE(body, ref i);
        if (i + 7 >= end)
        {
            return;
        }

        ulong mask = ReadUInt64LE(body, ref i);

        var st = _players.GetOrAdd(pid, _ => new PlayerState { Pid = pid });
        st.Npc = npc;
        st.KeysMask = mask;
        st.Seq = seq;
        st.LastSeenUtc = DateTime.UtcNow;
    }

    public void OnClientDisconnected(ClientSession s)
    {
        _sessions.TryRemove(s, out _);

        if (s.HandshakeDone)
        {
            if (_players.TryGetValue(s.AssignedPid, out var st))
            {
                st.KeysMask = 0;
                st.LastSeenUtc = DateTime.UtcNow;
            }
        }
    }

    public IReadOnlyCollection<PlayerState> SnapshotPlayers()
        => _players.Values.ToArray();

    public async Task BroadcastCamFrameAsync(byte[] frame, CancellationToken ct)
    {
        if (frame.Length == 0)
        {
            return;
        }

        uint camId = unchecked(_nextCamId++);
        var sessions = _sessions.Keys.ToArray();
        if (sessions.Length == 0)
        {
            return;
        }

        var tasks = new List<Task>(sessions.Length);
        foreach (var s in sessions)
        {
            if (!s.HandshakeDone)
            {
                continue;
            }

            tasks.Add(s.TrySendCamFrameAsync(camId, frame, MaxChunkBytes, ct));
        }

        if (tasks.Count == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(tasks);
        }
        catch
        {
            // ignore broadcast failures; session will close on its own.
        }
    }

    public async Task BroadcastSaveChunkAsync(uint saveSeq, uint totalLen, uint offset, byte[] chunk, CancellationToken ct)
    {
        var sessions = _sessions.Keys.ToArray();
        if (sessions.Length == 0)
        {
            return;
        }

        chunk ??= Array.Empty<byte>();
        if (chunk.Length > MaxChunkBytes)
        {
            // sender should already chunk, but clamp to keep protocol safe.
            Array.Resize(ref chunk, MaxChunkBytes);
        }

        byte[] payload = new byte[12 + chunk.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), saveSeq);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4, 4), totalLen);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8, 4), offset);
        if (chunk.Length > 0)
        {
            Buffer.BlockCopy(chunk, 0, payload, 12, chunk.Length);
        }

        var tasks = new List<Task>(sessions.Length);
        foreach (var s in sessions)
        {
            if (!s.HandshakeDone)
            {
                continue;
            }

            tasks.Add(s.SendPacketAsync(PacketType.SaveChunk, payload, ct));
        }

        if (tasks.Count == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(tasks);
        }
        catch
        {
            // ignore broadcast failures
        }
    }

    private static ushort ReadUInt16LE(byte[] buffer, ref int idx)
    {
        ushort value = (ushort)(buffer[idx] | (buffer[idx + 1] << 8));
        idx += 2;
        return value;
    }

    private static uint ReadUInt32LE(byte[] buffer, ref int idx)
    {
        uint value = (uint)(buffer[idx]
                            | (buffer[idx + 1] << 8)
                            | (buffer[idx + 2] << 16)
                            | (buffer[idx + 3] << 24));
        idx += 4;
        return value;
    }

    private static ulong ReadUInt64LE(byte[] buffer, ref int idx)
    {
        ulong value = (ulong)buffer[idx]
                      | ((ulong)buffer[idx + 1] << 8)
                      | ((ulong)buffer[idx + 2] << 16)
                      | ((ulong)buffer[idx + 3] << 24)
                      | ((ulong)buffer[idx + 4] << 32)
                      | ((ulong)buffer[idx + 5] << 40)
                      | ((ulong)buffer[idx + 6] << 48)
                      | ((ulong)buffer[idx + 7] << 56);
        idx += 8;
        return value;
    }
}
