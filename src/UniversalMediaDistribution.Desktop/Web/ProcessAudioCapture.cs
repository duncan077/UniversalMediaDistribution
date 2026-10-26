using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace UniversalMediaDistribution.Desktop.Web;

internal sealed partial class ProcessAudioCapture : IDisposable
{
    private readonly IAudioClient _client;
    private readonly IAudioCaptureClient _capture;
    private readonly AutoResetEvent _available = new(false);

    private ProcessAudioCapture(IAudioClient client, IAudioCaptureClient capture)
    {
        _client = client;
        _capture = capture;
    }

    public static unsafe ProcessAudioCapture Open(uint processId)
    {
        if (processId == 0 || processId == Environment.ProcessId)
            throw new ArgumentOutOfRangeException(nameof(processId), "Capture must target the browser, never the host process.");
        var parameters = new AudioActivationParameters { Type = 1, ProcessId = processId, Mode = 0 };
        var variant = new AudioActivationVariant { Type = 65, Size = (uint)sizeof(AudioActivationParameters), Data = (nint)(&parameters) };
        var handler = new ActivationHandler();
        Marshal.ThrowExceptionForHR(AudioInterop.ActivateAudioInterfaceAsync(
            "VAD\\Process_Loopback", typeof(IAudioClient).GUID, variant, handler, out var operation));
        IAudioClient? client = null;
        ProcessAudioCapture? result = null;
        try
        {
            // The API owns its callback until completion. Abandon marks late
            // results for release if activation times out.
            client = handler.TakeResult();
            Marshal.ThrowExceptionForHR(client.Initialize(0, 0x00060000, 200_000, 0, AudioFormat.StereoPcm, Guid.Empty));
            Marshal.ThrowExceptionForHR(client.GetService(typeof(IAudioCaptureClient).GUID, out var capture));
            result = new ProcessAudioCapture(client, AudioInterop.Take<IAudioCaptureClient>(capture));
            Marshal.ThrowExceptionForHR(client.SetEventHandle(result._available.SafeWaitHandle.DangerousGetHandle()));
            Marshal.ThrowExceptionForHR(client.Start());
            return result;
        }
        catch
        {
            if (result is not null) result.Dispose();
            else AudioInterop.Release(client);
            throw;
        }
        finally
        {
            handler.Abandon();
            Marshal.Release(operation);
        }
    }

    public unsafe void Drain(Action<byte[], int> consume, byte[] scratch)
    {
        while (true)
        {
            Marshal.ThrowExceptionForHR(_capture.GetNextPacketSize(out var next));
            if (next == 0) return;
            Marshal.ThrowExceptionForHR(_capture.GetBuffer(out var pointer, out var frames, out var flags, out _, out _));
            try
            {
                var bytes = checked((int)frames * 4);
                if (bytes > scratch.Length) throw new InvalidOperationException("Audio packet exceeds the relay buffer.");
                var data = scratch.AsSpan(0, bytes);
                if ((flags & 2) != 0) data.Clear();
                else new ReadOnlySpan<byte>((void*)pointer, bytes).CopyTo(data);
                consume(scratch, bytes);
            }
            finally { Marshal.ThrowExceptionForHR(_capture.ReleaseBuffer(frames)); }
        }
    }

    public void Dispose()
    {
        _client.Stop();
        AudioInterop.Release(_capture);
        AudioInterop.Release(_client);
        _available.Dispose();
    }

    [GeneratedComClass]
    private sealed partial class ActivationHandler : IAudioActivationHandler, IAudioAgileObject
    {
        private readonly object _gate = new();
        private bool _abandoned;
        private IAudioClient? _result;
        private TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IAudioClient TakeResult()
        {
            Completion.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            lock (_gate)
            {
                var result = _result!;
                _result = null;
                return result;
            }
        }

        public int ActivateCompleted(IAudioActivationOperation operation)
        {
            try
            {
                Marshal.ThrowExceptionForHR(operation.GetActivateResult(out var result, out var pointer));
                if (result < 0)
                {
                    if (pointer != 0) Marshal.Release(pointer);
                    Marshal.ThrowExceptionForHR(result);
                }
                var client = AudioInterop.Take<IAudioClient>(pointer);
                lock (_gate)
                {
                    if (_abandoned) AudioInterop.Release(client);
                    else
                    {
                        _result = client;
                        Completion.TrySetResult();
                    }
                }
            }
            catch (Exception exception) { Completion.TrySetException(exception); }
            return 0;
        }

        public void Abandon()
        {
            lock (_gate)
            {
                _abandoned = true;
                AudioInterop.Release(_result);
                _result = null;
            }
        }
    }
}
