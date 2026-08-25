using System.Runtime.InteropServices;
using System.Threading.Channels;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace NexusMonach.Services;

/// <summary>
/// Captures only audio rendered by the WebView2 browser process tree. Unlike
/// endpoint loopback, Nexus Voice is not fed back into Whisper, so capture can
/// remain continuous while translated speech is playing.
/// </summary>
internal static class ProcessAudioCaptureService
{
    internal static bool UsesDedicatedMtaActivation => true;

    public static Task<IContinuousAudioCaptureSession> StartAsync(int targetProcessId,
        int segmentMilliseconds, int overlapMilliseconds, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<IContinuousAudioCaptureSession>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var activationThread = new Thread(() =>
        {
            try
            {
                var session = StartCoreAsync(targetProcessId, segmentMilliseconds,
                        overlapMilliseconds, cancellationToken)
                    .GetAwaiter().GetResult();
                completion.TrySetResult(session);
            }
            catch (OperationCanceledException exception)
            {
                completion.TrySetCanceled(exception.CancellationToken);
            }
            catch (Exception exception) { completion.TrySetException(exception); }
        })
        {
            IsBackground = true,
            Name = "Nexus WebView2 audio activation"
        };
        activationThread.SetApartmentState(ApartmentState.MTA);
        activationThread.Start();
        return completion.Task.WaitAsync(cancellationToken);
    }

    private static async Task<IContinuousAudioCaptureSession> StartCoreAsync(int targetProcessId,
        int segmentMilliseconds, int overlapMilliseconds, CancellationToken cancellationToken)
    {
        var audioClient = await ProcessLoopbackInterop.ActivateAsync(
            checked((uint)targetProcessId), cancellationToken).ConfigureAwait(false);
        try
        {
            return new Session(audioClient, segmentMilliseconds, overlapMilliseconds);
        }
        catch
        {
            ReleaseComObject(audioClient);
            throw;
        }
    }

    private sealed class Session : IContinuousAudioCaptureSession
    {
        private readonly IAudioClient _audioClient;
        private readonly IAudioCaptureClientNative _captureClient;
        private readonly WaveFormat _waveFormat = new(44_100, 16, 2);
        private readonly EventWaitHandle _sampleReady = new(false, EventResetMode.AutoReset);
        private readonly CancellationTokenSource _stop = new();
        private readonly Channel<SystemAudioCaptureService.AudioSegment> _segments;
        private readonly MemoryStream _raw = new();
        private readonly object _sync = new();
        private readonly int _segmentMilliseconds;
        private readonly int _overlapBytes;
        private readonly Task _captureLoop;
        private readonly Task _segmentLoop;
        private long _sequence;
        private bool _disposed;
        private Exception? _captureError;

        public Session(IAudioClient audioClient, int segmentMilliseconds, int overlapMilliseconds)
        {
            _audioClient = audioClient;
            _segmentMilliseconds = Math.Clamp(segmentMilliseconds, 2_200, 12_000);
            _overlapBytes = Align(_waveFormat.AverageBytesPerSecond *
                                  Math.Clamp(overlapMilliseconds, 0, 1_500) / 1_000,
                _waveFormat.BlockAlign);
            _segments = Channel.CreateBounded<SystemAudioCaptureService.AudioSegment>(
                new BoundedChannelOptions(VideoDubbingPolicy.MaxBufferedSegments)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                    SingleWriter = true
                });

            var session = Guid.Empty;
            ThrowIfFailed(_audioClient.Initialize(AudioClientShareMode.Shared,
                AudioClientStreamFlags.Loopback |
                AudioClientStreamFlags.EventCallback |
                AudioClientStreamFlags.AutoConvertPcm |
                AudioClientStreamFlags.SrcDefaultQuality,
                0, 0, _waveFormat, ref session));

            var captureInterface = typeof(IAudioCaptureClientNative).GUID;
            ThrowIfFailed(_audioClient.GetService(captureInterface, out var service));
            _captureClient = (IAudioCaptureClientNative)service;
            ThrowIfFailed(_audioClient.SetEventHandle(_sampleReady.SafeWaitHandle.DangerousGetHandle()));
            ThrowIfFailed(_audioClient.Start());

            _captureLoop = Task.Run(CaptureLoop);
            _segmentLoop = Task.Run(SegmentLoopAsync);
        }

        public bool IsProcessIsolated => true;

        public IAsyncEnumerable<SystemAudioCaptureService.AudioSegment> ReadSegmentsAsync(
            CancellationToken cancellationToken = default) =>
            _segments.Reader.ReadAllAsync(cancellationToken);

        public void SuspendForDubbing() { }
        public void Resume() { }

        private void CaptureLoop()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    if (!_sampleReady.WaitOne(120)) continue;
                    if (_stop.IsCancellationRequested) break;
                    DrainAvailablePackets();
                }
            }
            catch (Exception ex)
            {
                _captureError = ex;
                _stop.Cancel();
            }
        }

        private void DrainAvailablePackets()
        {
            while (true)
            {
                ThrowIfFailed(_captureClient.GetNextPacketSize(out var frames));
                if (frames <= 0) return;

                ThrowIfFailed(_captureClient.GetBuffer(out var buffer, out var framesToRead,
                    out var flags, out _, out _));
                try
                {
                    var byteCount = checked(framesToRead * _waveFormat.BlockAlign);
                    if (byteCount <= 0) continue;
                    var bytes = new byte[byteCount];
                    if ((flags & AudioClientBufferFlags.Silent) == 0 && buffer != IntPtr.Zero)
                        Marshal.Copy(buffer, bytes, 0, byteCount);
                    lock (_sync)
                    {
                        if (!_disposed) _raw.Write(bytes, 0, bytes.Length);
                    }
                }
                finally
                {
                    ThrowIfFailed(_captureClient.ReleaseBuffer(framesToRead));
                }
            }
        }

        private async Task SegmentLoopAsync()
        {
            try
            {
                using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_segmentMilliseconds));
                while (await timer.WaitForNextTickAsync(_stop.Token))
                {
                    byte[] snapshot;
                    lock (_sync)
                    {
                        snapshot = _raw.ToArray();
                        var keep = Math.Min(_overlapBytes, snapshot.Length);
                        _raw.SetLength(0);
                        if (keep > 0) _raw.Write(snapshot, snapshot.Length - keep, keep);
                    }

                    if (snapshot.Length < _waveFormat.AverageBytesPerSecond * 5 / 4) continue;
                    var converted = SystemAudioCaptureService.ConvertRawToWav(snapshot, _waveFormat);
                    if (!VideoDubbingPolicy.IsAudible(converted.Rms, converted.Peak)) continue;
                    var duration = TimeSpan.FromSeconds(
                        snapshot.Length / (double)_waveFormat.AverageBytesPerSecond);
                    _segments.Writer.TryWrite(new SystemAudioCaptureService.AudioSegment(
                        Interlocked.Increment(ref _sequence), converted.Wav,
                        converted.Rms, converted.Peak, DateTimeOffset.UtcNow - duration));
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _captureError ??= ex; }
            finally { _segments.Writer.TryComplete(_captureError); }
        }

        public async ValueTask DisposeAsync()
        {
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
            }
            _stop.Cancel();
            _sampleReady.Set();
            try { await _captureLoop; } catch { }
            // IAudioClient.Stop invalidates the packet currently owned by
            // IAudioCaptureClient. Never race it against GetBuffer/ReleaseBuffer.
            try { _audioClient.Stop(); } catch { }
            try { await _segmentLoop; } catch { }
            _sampleReady.Dispose();
            _raw.Dispose();
            _stop.Dispose();
            ReleaseComObject(_captureClient);
            ReleaseComObject(_audioClient);
        }
    }

    private static void ThrowIfFailed(int result)
    {
        if (result < 0) Marshal.ThrowExceptionForHR(result);
    }

    private static int Align(int value, int blockAlign) =>
        blockAlign <= 1 ? value : value - value % blockAlign;

    private static void ReleaseComObject(object? value)
    {
        try { if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }
        catch { }
    }

    [ComImport]
    [Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClientNative
    {
        [PreserveSig]
        int GetBuffer(out IntPtr dataBuffer, out int framesToRead,
            out AudioClientBufferFlags bufferFlags, out long devicePosition, out long qpcPosition);

        [PreserveSig]
        int ReleaseBuffer(int framesRead);

        [PreserveSig]
        int GetNextPacketSize(out int framesInNextPacket);
    }

    private static class ProcessLoopbackInterop
    {
        private const string VirtualProcessLoopbackDevice = "VAD\\Process_Loopback";
        private const ushort VtBlob = 65;
        private static readonly Guid AudioClientInterface = typeof(IAudioClient).GUID;

        public static async Task<IAudioClient> ActivateAsync(uint targetProcessId,
            CancellationToken cancellationToken)
        {
            var parameters = new AudioClientActivationParameters
            {
                ActivationType = AudioClientActivationType.ProcessLoopback,
                ProcessLoopbackParameters = new AudioClientProcessLoopbackParameters
                {
                    TargetProcessId = targetProcessId,
                    ProcessLoopbackMode = ProcessLoopbackMode.IncludeTargetProcessTree
                }
            };

            var parametersPointer = Marshal.AllocHGlobal(Marshal.SizeOf<AudioClientActivationParameters>());
            var variantPointer = Marshal.AllocHGlobal(Marshal.SizeOf<PropVariant>());
            var completion = new TaskCompletionSource<object>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var handler = new ActivateCompletionHandler(completion);
            IActivateAudioInterfaceAsyncOperation? operation = null;
            try
            {
                Marshal.StructureToPtr(parameters, parametersPointer, false);
                Marshal.StructureToPtr(new PropVariant
                {
                    VariantType = VtBlob,
                    Blob = new Blob
                    {
                        Size = Marshal.SizeOf<AudioClientActivationParameters>(),
                        Data = parametersPointer
                    }
                }, variantPointer, false);

                var interfaceId = AudioClientInterface;
                ThrowIfFailed(ActivateAudioInterfaceAsync(VirtualProcessLoopbackDevice,
                    ref interfaceId, variantPointer, handler, out operation));
                var activated = await completion.Task.WaitAsync(TimeSpan.FromSeconds(10),
                    cancellationToken);
                return (IAudioClient)activated;
            }
            finally
            {
                GC.KeepAlive(handler);
                ReleaseComObject(operation);
                Marshal.FreeHGlobal(variantPointer);
                Marshal.FreeHGlobal(parametersPointer);
            }
        }

        [DllImport("Mmdevapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        private static extern int ActivateAudioInterfaceAsync(
            [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
            ref Guid interfaceId,
            IntPtr activationParameters,
            IActivateAudioInterfaceCompletionHandler completionHandler,
            out IActivateAudioInterfaceAsyncOperation activationOperation);

        [ComVisible(true)]
        [ClassInterface(ClassInterfaceType.None)]
        private sealed class ActivateCompletionHandler(
            TaskCompletionSource<object> completion) :
            IActivateAudioInterfaceCompletionHandler, IAgileObject
        {
            public int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation)
            {
                try
                {
                    ThrowIfFailed(operation.GetActivateResult(out var activationResult,
                        out var activatedInterface));
                    ThrowIfFailed(activationResult);
                    completion.TrySetResult(activatedInterface);
                }
                catch (Exception ex) { completion.TrySetException(ex); }
                return 0;
            }
        }

        [Guid("41D949AB-9862-444A-80F6-C261334DA5EB")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [ComImport]
        [ComVisible(true)]
        private interface IActivateAudioInterfaceCompletionHandler
        {
            [PreserveSig]
            int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation);
        }

        // ActivateAudioInterfaceAsync invokes the completion callback from the
        // MTA. Microsoft requires this marker so COM does not marshal it through
        // an apartment-bound proxy that can lose the requested audio interface.
        [Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [ComImport]
        [ComVisible(true)]
        private interface IAgileObject { }

        [ComImport]
        [Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IActivateAudioInterfaceAsyncOperation
        {
            [PreserveSig]
            int GetActivateResult(out int activateResult,
                [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct AudioClientActivationParameters
        {
            public AudioClientActivationType ActivationType;
            public AudioClientProcessLoopbackParameters ProcessLoopbackParameters;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct AudioClientProcessLoopbackParameters
        {
            public uint TargetProcessId;
            public ProcessLoopbackMode ProcessLoopbackMode;
        }

        private enum AudioClientActivationType
        {
            Default,
            ProcessLoopback
        }

        private enum ProcessLoopbackMode
        {
            IncludeTargetProcessTree,
            ExcludeTargetProcessTree
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct PropVariant
        {
            [FieldOffset(0)] public ushort VariantType;
            [FieldOffset(8)] public Blob Blob;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Blob
        {
            public int Size;
            public IntPtr Data;
        }
    }
}
