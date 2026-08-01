using NAudio.Wave;

namespace NexusMonach.Services;

public static class MicrophoneCaptureService
{
    public static async Task<byte[]> CaptureWavAsync(TimeSpan duration, CancellationToken cancellationToken)
    {
        duration = TimeSpan.FromMilliseconds(Math.Clamp(duration.TotalMilliseconds, 750, 10_000));
        using var capture = new WaveInEvent
        {
            DeviceNumber = -1,
            WaveFormat = new WaveFormat(16_000, 16, 1),
            BufferMilliseconds = 100
        };
        using var stream = new MemoryStream();
        var writer = new WaveFileWriter(new NonClosingStream(stream), capture.WaveFormat);
        var stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        capture.DataAvailable += (_, args) => writer.Write(args.Buffer, 0, args.BytesRecorded);
        capture.RecordingStopped += (_, args) =>
        {
            if (args.Exception is not null) stopped.TrySetException(args.Exception);
            else stopped.TrySetResult(true);
        };

        using var registration = cancellationToken.Register(() =>
        {
            try { capture.StopRecording(); } catch { }
        });
        capture.StartRecording();
        try { await Task.Delay(duration, cancellationToken); }
        catch (OperationCanceledException) { }
        finally { try { capture.StopRecording(); } catch { } }
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellationToken.ThrowIfCancellationRequested();
        writer.Dispose();
        return stream.ToArray();
    }

    private sealed class NonClosingStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        protected override void Dispose(bool disposing) { }
    }
}
