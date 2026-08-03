using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace VoiceScreen.App.Audio;

internal static class SampleProviderChannels
{
    public static ISampleProvider ToMono(ISampleProvider source)
    {
        if (source.WaveFormat.Channels == 1) return source;
        if (source.WaveFormat.Channels == 2)
            return new StereoToMonoSampleProvider(source) { LeftVolume = 0.5f, RightVolume = 0.5f };
        return new MultiChannelToMonoSampleProvider(source);
    }

    public static ISampleProvider ToStereo(ISampleProvider source)
    {
        if (source.WaveFormat.Channels == 2) return source;
        return new MonoToStereoSampleProvider(ToMono(source));
    }
}

internal sealed class MultiChannelToMonoSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private float[] _sourceBuffer = Array.Empty<float>();

    public MultiChannelToMonoSampleProvider(ISampleProvider source)
    {
        _source = source;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        var channels = _source.WaveFormat.Channels;
        var required = count * channels;
        if (_sourceBuffer.Length < required) _sourceBuffer = new float[required];
        var read = _source.Read(_sourceBuffer, 0, required);
        var frames = read / channels;
        for (var frame = 0; frame < frames; frame++)
        {
            var sum = 0f;
            for (var channel = 0; channel < channels; channel++) sum += _sourceBuffer[frame * channels + channel];
            buffer[offset + frame] = sum / channels;
        }
        return frames;
    }
}
