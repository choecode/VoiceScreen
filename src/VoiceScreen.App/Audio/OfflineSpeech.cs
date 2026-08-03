using System.Speech.Synthesis;
using VoiceScreen.App.Models;

namespace VoiceScreen.App.Audio;

public static class OfflineSpeech
{
    public static IReadOnlyList<SpeechVoiceOption> GetInstalledEnglishVoices()
    {
        using var synthesizer = new SpeechSynthesizer();
        return synthesizer.GetInstalledVoices()
            .Where(voice => voice.Enabled
                && voice.VoiceInfo.Culture.TwoLetterISOLanguageName.Equals("en", StringComparison.OrdinalIgnoreCase))
            .Select(voice => new SpeechVoiceOption(
                voice.VoiceInfo.Name,
                $"{voice.VoiceInfo.Name} · {DescribeGender(voice.VoiceInfo.Gender)} · {voice.VoiceInfo.Culture.DisplayName}"))
            .ToArray();
    }

    public static Task<byte[]> SynthesizeEnglishAsync(string text, CancellationToken cancellationToken,
        string? voiceName = null)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var synthesizer = new SpeechSynthesizer();
            var englishVoices = synthesizer.GetInstalledVoices()
                .Where(voice => voice.Enabled
                    && voice.VoiceInfo.Culture.TwoLetterISOLanguageName.Equals("en", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var englishVoice = englishVoices
                .FirstOrDefault(voice => voice.VoiceInfo.Name.Equals(voiceName, StringComparison.OrdinalIgnoreCase))
                ?? englishVoices
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

    private static string DescribeGender(VoiceGender gender) => gender switch
    {
        VoiceGender.Male => "男声",
        VoiceGender.Female => "女声",
        VoiceGender.Neutral => "中性",
        _ => "未标注"
    };
}
