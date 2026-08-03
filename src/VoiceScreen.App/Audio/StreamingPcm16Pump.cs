using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace VoiceScreen.App.Audio;

public sealed class StreamingPcm16Pump : IAsyncDisposable
{
    private readonly BufferedWaveProvider _input;
    private readonly ISampleProvider _resampled;
    private readonly CancellationTokenSource _cts = new();
    private Task? _pumpTask;

    public StreamingPcm16Pump(WaveFormat inputFormat)
    {
        _input = new BufferedWaveProvider(inputFormat)
        {
            BufferDuration = TimeSpan.FromSeconds(3),
            DiscardOnBufferOverflow = true,
            ReadFully = true
        };
        var mono = SampleProviderChannels.ToMono(_input.ToSampleProvider());
        _resampled = new WdlResamplingSampleProvider(mono, 16000);
    }

    public event Func<byte[], CancellationToken, ValueTask>? FrameReady;

    public void AddSamples(byte[] buffer, int count) => _input.AddSamples(buffer, 0, count);

    public void Start()
    {
        _pumpTask ??= Task.Run(PumpAsync);
    }

    private async Task PumpAsync()
    {
        var samples = new float[640];
        while (!_cts.IsCancellationRequested)
        {
            Array.Clear(samples);
            _resampled.Read(samples, 0, samples.Length);
            var bytes = new byte[1280];
            for (var i = 0; i < samples.Length; i++)
            {
                var sample = (short)Math.Clamp(samples[i] * short.MaxValue, short.MinValue, short.MaxValue);
                bytes[i * 2] = (byte)(sample & 0xff);
                bytes[i * 2 + 1] = (byte)((sample >> 8) & 0xff);
            }
            var handler = FrameReady;
            if (handler is not null) await handler(bytes, _cts.Token).ConfigureAwait(false);
            await Task.Delay(40, _cts.Token).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_pumpTask is not null)
        {
            try { await _pumpTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        _cts.Dispose();
    }
}
