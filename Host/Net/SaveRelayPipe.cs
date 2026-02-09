using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Host;

public sealed class SaveRelayPipe : IDisposable
{
    private readonly string _pipeName;
    private readonly TcpHostServer _server;
    private NamedPipeServerStream? _pipe;
    private long _receivedChunks;
    private int _lastBytes;
    private int _readInProgress;

    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("LCOS");
    private const byte Version = 1;

    public SaveRelayPipe(string pipeName, TcpHostServer server)
    {
        _pipeName = pipeName;
        _server = server;
    }

    public Task RunAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _readInProgress, 1) == 1)
        {
            return Task.CompletedTask;
        }

        return Task.Run(() => ReadLoopOuterAsync(ct), ct);
    }

    public (long Chunks, int LastBytes, bool Connected) GetStats()
    {
        var p = _pipe;
        bool connected = p != null && p.IsConnected;
        return (_receivedChunks, _lastBytes, connected);
    }

    public void Dispose()
    {
        try
        {
            _pipe?.Dispose();
        }
        catch
        {
        }

        _pipe = null;
        Interlocked.Exchange(ref _readInProgress, 0);
    }

    private async Task ReadLoopOuterAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                using var pipe = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    0,
                    64 * 1024);

                _pipe = pipe;
                try
                {
                    await pipe.WaitForConnectionAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                try
                {
                    await ReadLoopAsync(pipe, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    // ignore and restart
                }
            }
        }
        finally
        {
            _pipe = null;
            Interlocked.Exchange(ref _readInProgress, 0);
        }
    }

    private async Task ReadLoopAsync(Stream s, CancellationToken ct)
    {
        var lenBuf = new byte[4];
        while (!ct.IsCancellationRequested)
        {
            await ReadExactAsync(s, lenBuf, 0, 4, ct);
            uint len = BinaryPrimitives.ReadUInt32LittleEndian(lenBuf);
            if (len == 0 || len > 8_000_000)
            {
                return;
            }

            var frame = new byte[len];
            await ReadExactAsync(s, frame, 0, (int)len, ct);

            _lastBytes = frame.Length;
            if (frame.Length < 4 + 1 + 4 + 4 + 4)
            {
                continue;
            }

            if (frame[0] != Magic[0] || frame[1] != Magic[1] || frame[2] != Magic[2] || frame[3] != Magic[3])
            {
                continue;
            }

            if (frame[4] != Version)
            {
                continue;
            }

            uint saveSeq = BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(5, 4));
            uint totalLen = BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(9, 4));
            uint offset = BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(13, 4));
            int chunkLen = frame.Length - 17;

            byte[] chunk = Array.Empty<byte>();
            if (chunkLen > 0)
            {
                chunk = new byte[chunkLen];
                Buffer.BlockCopy(frame, 17, chunk, 0, chunkLen);
            }

            Interlocked.Increment(ref _receivedChunks);
            await _server.BroadcastSaveChunkAsync(saveSeq, totalLen, offset, chunk, ct);
        }
    }

    private static async Task ReadExactAsync(Stream s, byte[] buf, int offset, int count, CancellationToken ct)
    {
        int readTotal = 0;
        while (readTotal < count)
        {
            int n = await s.ReadAsync(buf.AsMemory(offset + readTotal, count - readTotal), ct);
            if (n == 0)
            {
                throw new EndOfStreamException();
            }

            readTotal += n;
        }
    }
}

