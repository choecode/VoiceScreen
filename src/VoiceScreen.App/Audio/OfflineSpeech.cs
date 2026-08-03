using System.Speech.Synthesis;

namespace VoiceScreen.App.Audio;

public static class OfflineSpeech
{
    public static Task<byte[]> SynthesizeEnglishAsync(string text, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var synthesizer = new SpeechSynthesizer();
            var englishVoice = synthesizer.GetInstalledVoices()
                .FirstOrDefault(voice => voice.Enabled
                    && voice.VoiceInfo.Culture.TwoLetterISOLanguageName.Equals("en", StringComparison.OrdinalIgnoreCase));
            if (englishVoice is not null)
                synthesizer.SelectVoice(englishVoice.VoiceInfo.Name);
            synthesizer.Rate = 1;
            synthesizer.Volume = 90;
            using var wave = new MemoryStream();
            synthesizer.SetOutputToWaveStream(wave);
            synthesizer.Speak(text);
            synthesizer.SetOutputToNull();
            return AudioTranscoder.WaveToPcm16Mono16Khz(wave);
        }, cancellationToken);
    }
}
