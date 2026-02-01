using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Threading;

namespace Host;

public sealed class RubyBridgePipe : IDisposable
{
    private readonly string _pipeName;
    private readonly object _sync = new();
    private NamedPipeServerStream? _pipe;
    private long _sentFrames;
    private int _sentBytesLast;
    private long _droppedFrames;
    private long _timeouts;
    private int _writeInProgress;

    private static readonly TimeSpan WriteTimeout = TimeSpan.FromMilliseconds(20);

    public RubyBridgePipe(string pipeName)
    {
        _pipeName = pipeName;
        RestartPipe();
    }

    public void SendSnapshot(IReadOnlyCollection<PlayerState> players, TimeSpan staleTimeout)
    {
        byte[] payload = BuildPayload(players, staleTimeout);
        if (payload.Length == 0)
        {
            return;
        }

        if (payload.Length > ushort.MaxValue)
        {
            return;
        }

        byte[] frame = new byte[2 + payload.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(0, 2), (ushort)payload.Length);
        Buffer.BlockCopy(payload, 0, frame, 2, payload.Length);

        if (Interlocked.Exchange(ref _writeInProgress, 1) == 1)
        {
            Interlocked.Increment(ref _droppedFrames);
            return;
        }

        try
        {
            lock (_sync)
            {
                if (_pipe == null || !_pipe.IsConnected)
                {
                    return;
                }

                try
                {
                    using var cts = new CancellationTokenSource(WriteTimeout);
                    _pipe.WriteAsync(frame, 0, frame.Length, cts.Token).GetAwaiter().GetResult();
                    _sentFrames++;
                    _sentBytesLast = frame.Length;
                }
                catch (OperationCanceledException)
                {
                    _timeouts++;
                    RestartPipe();
                }
                catch (IOException)
                {
                    RestartPipe();
                }
                catch (ObjectDisposedException)
                {
                    RestartPipe();
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _writeInProgress, 0);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _pipe?.Dispose();
            _pipe = null;
        }
    }

    public (long Frames, int LastBytes, bool Connected, long Dropped, long Timeouts) GetStats()
    {
        lock (_sync)
        {
            bool connected = _pipe != null && _pipe.IsConnected;
            return (_sentFrames, _sentBytesLast, connected, _droppedFrames, _timeouts);
        }
    }

    private void RestartPipe()
    {
        lock (_sync)
        {
            _pipe?.Dispose();
            _pipe = new NamedPipeServerStream(
                _pipeName,
                PipeDirection.Out,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous,
                0,
                64 * 1024);

            _ = _pipe.WaitForConnectionAsync();
        }
    }

    private static byte[] BuildPayload(IReadOnlyCollection<PlayerState> players, TimeSpan staleTimeout)
    {
        int count = Math.Min(players.Count, 255);
        byte[] buf = new byte[8 + count * 16];

        buf[0] = (byte)'L';
        buf[1] = (byte)'C';
        buf[2] = (byte)'O';
        buf[3] = (byte)'1';
        buf[4] = 1;
        buf[5] = (byte)count;
        buf[6] = 0;
        buf[7] = 0;

        int off = 8;
        int written = 0;
        var now = DateTime.UtcNow;

        foreach (var p in players)
        {
            if (written >= count)
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

        if (written == 0)
        {
            buf[5] = 0;
            return buf;
        }

        int totalLen = 8 + written * 16;
        if (totalLen != buf.Length)
        {
            Array.Resize(ref buf, totalLen);
            buf[5] = (byte)written;
        }

        return buf;
    }
}
