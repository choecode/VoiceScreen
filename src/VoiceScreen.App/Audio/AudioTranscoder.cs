using System.IO;
using NAudio.Wave;

namespace VoiceScreen.App.Audio;

public static class AudioTranscoder
{
    public static byte[] ToPcm16Mono16Khz(CapturedAudio captured)
    {
        if (captured.Data.Length == 0) return Array.Empty<byte>();
        using var inputStream = new MemoryStream(captured.Data, writable: false);
        using var raw = new RawSourceWaveStream(inputStream, captured.Format);
        using var resampler = new MediaFoundationResampler(raw, new WaveFormat(16000, 16, 1))
        {
            ResamplerQuality = 60
        };
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = resampler.Read(buffer, 0, buffer.Length)) > 0) output.Write(buffer, 0, read);
        return output.ToArray();
    }

    public static byte[] WaveToPcm16Mono16Khz(Stream waveStream)
    {
        waveStream.Position = 0;
        using var reader = new WaveFileReader(waveStream);
        using var resampler = new MediaFoundationResampler(reader, new WaveFormat(16000, 16, 1)) { ResamplerQuality = 60 };
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = resampler.Read(buffer, 0, buffer.Length)) > 0) output.Write(buffer, 0, read);
        return output.ToArray();
    }
}
