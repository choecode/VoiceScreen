using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using NAudio.Wave;
using VoiceScreen.App.Diagnostics;
using VoiceScreen.App.Models;

namespace VoiceScreen.App.Services;

/// <summary>调用用户自建的 VoiceScreen OPUS-MT + Piper 服务，不使用任何收费 API。</summary>
public sealed class SelfHostedApiService : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _englishVoice;

    public SelfHostedApiService(string baseUrl, string? englishVoice = null)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("自建服务地址无效，请填写完整的 http:// 或 https:// 地址。");
        _http = new HttpClient { BaseAddress = uri, Timeout = TimeSpan.FromSeconds(60) };
        _englishVoice = string.IsNullOrWhiteSpace(englishVoice) ? "en_US-lessac-medium" : englishVoice.Trim();
    }

    public async Task<IReadOnlyList<PiperVoiceOption>> GetEnglishVoicesAsync(CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync("providers", cancellationToken).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("providers", out var providers)) return [];
        foreach (var provider in providers.EnumerateArray())
        {
            if (!provider.TryGetProperty("id", out var id) || id.GetString() != "local-opus") continue;
            if (!provider.TryGetProperty("voices", out var voices)
                || !voices.TryGetProperty("zh-en", out var english)) return [];
            provider.TryGetProperty("voiceLabels", out var labels);
            provider.TryGetProperty("voiceLicenses", out var licenses);
            provider.TryGetProperty("voiceAvailability", out var availability);
            var result = new List<PiperVoiceOption>();
            foreach (var item in english.EnumerateArray())
            {
                var voice = item.GetString();
                if (string.IsNullOrWhiteSpace(voice)) continue;
                if (availability.ValueKind == JsonValueKind.Object
                    && availability.TryGetProperty(voice, out var installed)
                    && installed.ValueKind == JsonValueKind.False) continue;
                var label = ReadStringMap(labels, voice, voice);
                var license = ReadStringMap(licenses, voice, "许可证未声明");
                result.Add(new PiperVoiceOption(voice, label, license));
            }
            return result;
        }
        return [];
    }

    public async Task<string> TestAsync(CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        var healthBody = await CheckHealthAsync(cancellationToken).ConfigureAwait(false);
        var translated = await TranslateChineseWithSpeechAsync("敌人在二楼，我们从左边走。",
            cancellationToken).ConfigureAwait(false);
        timer.Stop();
        return $"自建翻译与 Piper 可用 · 完整链路 {timer.ElapsedMilliseconds} ms · " +
               $"voice={_englishVoice} · {translated.EnglishText} · " +
               $"PCM {translated.Pcm16Mono16Khz.Length} bytes · health={healthBody}";
    }

    public async Task<string> CheckHealthAsync(CancellationToken cancellationToken)
    {
        using var health = await _http.GetAsync("health", cancellationToken).ConfigureAwait(false);
        var body = await health.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        health.EnsureSuccessStatusCode();
        return body;
    }

    public async Task<string> TranslateChineseAsync(string text, CancellationToken cancellationToken)
        => (await TranslateAsync(text, "zh-en", false, cancellationToken).ConfigureAwait(false)).Text;

    public async Task<SelfHostedSpeechResult> TranslateChineseWithSpeechAsync(string chineseText,
        CancellationToken cancellationToken)
    {
        var result = await TranslateAsync(chineseText, "zh-en", true, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(result.AudioUrl))
            throw new InvalidOperationException("自建服务没有返回 Piper 音频地址。");
        var pcm = await DownloadPcmAsync(result.AudioUrl, cancellationToken).ConfigureAwait(false);
        return new SelfHostedSpeechResult(result.Text, pcm);
    }

    public async Task<LocalIncomingTranslation> TranslateIncomingAsync(string text, string language,
        CancellationToken cancellationToken)
    {
        if (text.Any(c => c is >= '\u3400' and <= '\u9fff'))
            return new LocalIncomingTranslation(text.Trim(), text.Trim(), "zh");
        var direction = language.StartsWith("th", StringComparison.OrdinalIgnoreCase)
                        || text.Any(c => c is >= '\u0e00' and <= '\u0e7f')
            ? "th-zh"
            : "en-zh";
        var translated = await TranslateAsync(text, direction, false, cancellationToken).ConfigureAwait(false);
        return new LocalIncomingTranslation(text.Trim(), translated.Text, direction == "th-zh" ? "th" : "en");
    }

    private async Task<byte[]> DownloadPcmAsync(string audioUrl, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(audioUrl.TrimStart('/'), cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var waveBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        using var stream = new MemoryStream(waveBytes, writable: false);
        using var reader = new WaveFileReader(stream);
        using var resampler = new MediaFoundationResampler(reader, new WaveFormat(16000, 16, 1)) { ResamplerQuality = 60 };
        using var pcm = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = resampler.Read(buffer, 0, buffer.Length)) > 0) pcm.Write(buffer, 0, read);
        return pcm.ToArray();
    }

    private async Task<EvaluationResult> TranslateAsync(string text, string direction, bool includeTts,
        CancellationToken cancellationToken)
    {
        var payload = new
        {
            provider = "local-opus",
            text,
            direction,
            useGlossary = true,
            beamSize = 4,
            maxDecodingLength = 128,
            includeTts,
            voice = includeTts ? _englishVoice : null
        };
        using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync("evaluate", content, cancellationToken).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"自建翻译服务失败（HTTP {(int)response.StatusCode}）：{ReadError(json)}");
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var translated = root.TryGetProperty("translatedText", out var translatedElement)
            ? translatedElement.GetString()?.Trim() : null;
        if (string.IsNullOrWhiteSpace(translated)) throw new InvalidOperationException("自建服务返回了空译文。");
        var audioUrl = root.TryGetProperty("tts", out var tts) && tts.ValueKind == JsonValueKind.Object
                       && tts.TryGetProperty("audioUrl", out var audio)
            ? audio.GetString()
            : null;
        VoiceScreenLog.Info($"Self-hosted evaluate direction={direction} tts={includeTts}");
        return new EvaluationResult(translated, audioUrl);
    }

    private static string ReadError(string json)
    {
        try { return JsonDocument.Parse(json).RootElement.GetProperty("error").GetString() ?? json; }
        catch { return json; }
    }

    private static string ReadStringMap(JsonElement map, string key, string fallback)
        => map.ValueKind == JsonValueKind.Object && map.TryGetProperty(key, out var value)
            ? value.GetString() ?? fallback
            : fallback;

    public void Dispose() => _http.Dispose();
    private sealed record EvaluationResult(string Text, string? AudioUrl);
}

public sealed record SelfHostedSpeechResult(string EnglishText, byte[] Pcm16Mono16Khz);
