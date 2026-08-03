using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace VoiceScreen.App.Audio;

public sealed class EndpointLoopbackCapture : IAsyncDisposable, IDisposable
{
    private WasapiLoopbackCapture? _capture;
    private StreamingPcm16Pump? _pump;

    public event Func<byte[], CancellationToken, ValueTask>? FrameReady;

    public void Start(string renderDeviceId)
    {
        using var enumerator = new MMDeviceEnumerator();
        var device = enumerator.GetDevice(renderDeviceId);
        _capture = new WasapiLoopbackCapture(device);
        _pump = new StreamingPcm16Pump(_capture.WaveFormat);
        _pump.FrameReady += ForwardFrameAsync;
        _capture.DataAvailable += OnDataAvailable;
        _pump.Start();
        _capture.StartRecording();
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e) => _pump?.AddSamples(e.Buffer, e.BytesRecorded);

    private ValueTask ForwardFrameAsync(byte[] frame, CancellationToken ct)
        => FrameReady?.Invoke(frame, ct) ?? ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (_capture is not null)
        {
            _capture.DataAvailable -= OnDataAvailable;
            try { _capture.StopRecording(); } catch { }
            _capture.Dispose();
        }
        if (_pump is not null)
        {
            _pump.FrameReady -= ForwardFrameAsync;
            await _pump.DisposeAsync();
        }
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
