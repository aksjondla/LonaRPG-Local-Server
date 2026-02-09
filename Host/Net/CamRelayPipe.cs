using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

namespace Host;

public sealed class CamRelayPipe : IDisposable
{
    private readonly string _pipeName;
    private NamedPipeServerStream? _pipe;
    private byte[]? _latestFrame;
    private long _receivedFrames;
    private int _lastBytes;
    private int _readInProgress;

    public CamRelayPipe(string pipeName)
    {
        _pipeName = pipeName;
    }

    public Task RunAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _readInProgress, 1) == 1)
        {
            return Task.CompletedTask;
        }

        return Task.Run(() => ReadLoopOuterAsync(ct), ct);
    }

    public bool TryConsumeLatest(out byte[]? frame)
    {
        frame = Interlocked.Exchange(ref _latestFrame, null);
        return frame != null;
    }

    public (long Frames, int LastBytes, bool Connected) GetStats()
    {
        var p = _pipe;
        bool connected = p != null && p.IsConnected;
        return (_receivedFrames, _lastBytes, connected);
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
        Interlocked.Exchange(ref _latestFrame, null);
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
                    // ignore and restart the pipe
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
            if (len == 0 || len > 4_000_000)
            {
                return;
            }

            var payload = new byte[len];
            await ReadExactAsync(s, payload, 0, (int)len, ct);

            _lastBytes = payload.Length;
            Interlocked.Increment(ref _receivedFrames);
            Interlocked.Exchange(ref _latestFrame, payload);
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

