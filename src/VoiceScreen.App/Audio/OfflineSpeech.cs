using System.Globalization;
using VoiceScreen.App.Diagnostics;
using VoiceScreen.App.Models;
using Windows.Media.SpeechSynthesis;
using SapiSpeechSynthesizer = System.Speech.Synthesis.SpeechSynthesizer;
using WindowsSpeechSynthesizer = Windows.Media.SpeechSynthesis.SpeechSynthesizer;

namespace VoiceScreen.App.Audio;

public static class OfflineSpeech
{
    private const string WindowsSpeechVoicePrefix = "windows-speech:";

    public static IReadOnlyList<SpeechVoiceOption> GetInstalledEnglishVoices()
    {
        var voices = new List<SpeechVoiceOption>();
        var windowsVoiceNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var voice in WindowsSpeechSynthesizer.AllVoices.Where(IsEnglishVoice))
            {
                windowsVoiceNames.Add(voice.DisplayName);
                voices.Add(new SpeechVoiceOption(
                    WindowsSpeechVoicePrefix + voice.Id,
                    $"{voice.DisplayName} · {DescribeGender(voice.Gender)} · {DescribeCulture(voice.Language)} · Windows 新版"));
            }
        }
        catch (Exception ex)
        {
            // WinRT 枚举失败时仍保留传统 SAPI，避免语音设置或系统组件异常拖垮整个应用。
            VoiceScreenLog.Warn($"Windows Speech voice enumeration failed; falling back to SAPI: {ex.Message}");
        }

        using var synthesizer = new SapiSpeechSynthesizer();
        voices.AddRange(synthesizer.GetInstalledVoices()
            .Where(voice => voice.Enabled
                            && voice.VoiceInfo.Culture.TwoLetterISOLanguageName.Equals(
                                "en", StringComparison.OrdinalIgnoreCase)
                            && !windowsVoiceNames.Contains(voice.VoiceInfo.Name))
            .Select(voice => new SpeechVoiceOption(
                voice.VoiceInfo.Name,
                $"{voice.VoiceInfo.Name} · {DescribeGender(voice.VoiceInfo.Gender)} · {voice.VoiceInfo.Culture.DisplayName} · Windows 兼容")));

        VoiceScreenLog.Info(
            $"English TTS voices enumerated: windows-speech={voices.Count(voice => IsWindowsSpeechVoiceId(voice.Id))}, sapi={voices.Count(voice => !IsWindowsSpeechVoiceId(voice.Id))}");
        return voices;
    }

    public static async Task<byte[]> SynthesizeEnglishAsync(string text, CancellationToken cancellationToken,
        string? voiceName = null)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var windowsVoiceId = GetWindowsSpeechVoiceId(voiceName)
                             ?? (string.IsNullOrWhiteSpace(voiceName)
                                 ? WindowsSpeechSynthesizer.AllVoices.FirstOrDefault(IsEnglishVoice)?.Id
                                 : null);
        if (!string.IsNullOrWhiteSpace(windowsVoiceId))
        {
            try
            {
                return await SynthesizeWithWindowsSpeechAsync(text, windowsVoiceId, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                VoiceScreenLog.Warn(
                    $"Windows Speech synthesis failed for voice={windowsVoiceId}; falling back to SAPI: {ex.Message}");
            }
        }

        return await SynthesizeWithSapiAsync(text, voiceName, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte[]> SynthesizeWithWindowsSpeechAsync(string text, string voiceId,
        CancellationToken cancellationToken)
    {
        var voice = WindowsSpeechSynthesizer.AllVoices.FirstOrDefault(candidate =>
            candidate.Id.Equals(voiceId, StringComparison.OrdinalIgnoreCase) && IsEnglishVoice(candidate))
            ?? throw new InvalidOperationException("选择的新版 Windows 英文音色已不可用，请刷新音色列表。");

        using var synthesizer = new WindowsSpeechSynthesizer { Voice = voice };
        using var speechStream = await synthesizer.SynthesizeTextToStreamAsync(text)
            .AsTask(cancellationToken).ConfigureAwait(false);
        speechStream.Seek(0);
        using var input = speechStream.AsStreamForRead();
        using var wave = new MemoryStream();
        await input.CopyToAsync(wave, cancellationToken).ConfigureAwait(false);
        return AudioTranscoder.WaveToPcm16Mono16Khz(wave);
    }

    private static Task<byte[]> SynthesizeWithSapiAsync(string text, string? voiceName,
        CancellationToken cancellationToken) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var synthesizer = new SapiSpeechSynthesizer();
        var englishVoices = synthesizer.GetInstalledVoices()
            .Where(voice => voice.Enabled
                            && voice.VoiceInfo.Culture.TwoLetterISOLanguageName.Equals(
                                "en", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var englishVoice = englishVoices
                               .FirstOrDefault(voice => voice.VoiceInfo.Name.Equals(
                                   voiceName, StringComparison.OrdinalIgnoreCase))
                           ?? englishVoices.FirstOrDefault();
        if (englishVoice is null)
            throw new InvalidOperationException("没有检测到可用的 Windows 英文音色。");

        synthesizer.SelectVoice(englishVoice.VoiceInfo.Name);
        synthesizer.Rate = 1;
        synthesizer.Volume = 90;
        using var wave = new MemoryStream();
        synthesizer.SetOutputToWaveStream(wave);
        synthesizer.Speak(text);
        synthesizer.SetOutputToNull();
        return AudioTranscoder.WaveToPcm16Mono16Khz(wave);
    }, cancellationToken);

    private static bool IsEnglishVoice(VoiceInformation voice) =>
        voice.Language.StartsWith("en", StringComparison.OrdinalIgnoreCase);

    private static bool IsWindowsSpeechVoiceId(string? voiceId) =>
        voiceId?.StartsWith(WindowsSpeechVoicePrefix, StringComparison.OrdinalIgnoreCase) == true;

    private static string? GetWindowsSpeechVoiceId(string? voiceId) =>
        IsWindowsSpeechVoiceId(voiceId) ? voiceId![WindowsSpeechVoicePrefix.Length..] : null;

    private static string DescribeCulture(string language)
    {
        try
        {
            return CultureInfo.GetCultureInfo(language).DisplayName;
        }
        catch (CultureNotFoundException)
        {
            return language;
        }
    }

    private static string DescribeGender(System.Speech.Synthesis.VoiceGender gender) => gender switch
    {
        System.Speech.Synthesis.VoiceGender.Male => "男声",
        System.Speech.Synthesis.VoiceGender.Female => "女声",
        System.Speech.Synthesis.VoiceGender.Neutral => "中性",
        _ => "未标注"
    };

    private static string DescribeGender(Windows.Media.SpeechSynthesis.VoiceGender gender) => gender switch
    {
        Windows.Media.SpeechSynthesis.VoiceGender.Male => "男声",
        Windows.Media.SpeechSynthesis.VoiceGender.Female => "女声",
        _ => "未标注"
    };
}
