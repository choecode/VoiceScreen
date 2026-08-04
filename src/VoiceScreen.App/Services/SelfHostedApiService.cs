using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using NAudio.Wave;
using VoiceScreen.App.Diagnostics;
using VoiceScreen.App.Models;

namespace VoiceScreen.App.Services;

/// <summary>本机助手进程代理无密钥 MyMemory 翻译与 Edge TTS；语音识别仍在本机完成。</summary>
public sealed class OnlineApiService : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _englishVoice;

    public OnlineApiService(string? englishVoice = null)
    {
        _http = new HttpClient
        {
            BaseAddress = new Uri("http://127.0.0.1:18765/"),
            Timeout = TimeSpan.FromSeconds(60)
        };
        _englishVoice = string.IsNullOrWhiteSpace(englishVoice) ? "en-US-JennyNeural" : englishVoice.Trim();
    }

    public async Task<IReadOnlyList<ApiVoiceOption>> GetEnglishVoicesAsync(CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync("providers", cancellationToken).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("providers", out var providers)) return [];
        foreach (var provider in providers.EnumerateArray())
        {
            if (!provider.TryGetProperty("id", out var id) || id.GetString() != "mymemory-edge") continue;
            if (!provider.TryGetProperty("voices", out var voices)
                || !voices.TryGetProperty("zh-en", out var english)) return [];
            var result = new List<ApiVoiceOption>();
            foreach (var item in english.EnumerateArray())
            {
                var voice = item.GetString();
                if (string.IsNullOrWhiteSpace(voice)) continue;
                var label = voice.Contains("Guy", StringComparison.OrdinalIgnoreCase)
                    ? "Guy · 美式英文男声"
                    : "Jenny · 美式英文女声";
                result.Add(new ApiVoiceOption(voice, label));
            }
            return result;
        }
        return [];
    }

    public async Task<string> TestAsync(CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        var healthBody = await CheckHealthAsync(cancellationToken).ConfigureAwait(false);
        var english = await TranslateChineseAsync("敌人在二楼，我们从左边走。",
            cancellationToken).ConfigureAwait(false);
        var pcm = await SynthesizeEnglishAsync(english, cancellationToken).ConfigureAwait(false);
        timer.Stop();
        return $"纯 API 翻译与 Edge TTS 可用 · 完整链路 {timer.ElapsedMilliseconds} ms · " +
               $"voice={_englishVoice} · {english} · " +
               $"PCM {pcm.Length} bytes · health={healthBody}";
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

    public async Task<OnlineSpeechResult> TranslateChineseWithSpeechAsync(string chineseText,
        CancellationToken cancellationToken)
    {
        var result = await TranslateAsync(chineseText, "zh-en", true, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(result.AudioUrl))
            throw new InvalidOperationException("Edge TTS 没有返回音频地址。");
        var pcm = await DownloadPcmAsync(result.AudioUrl, cancellationToken).ConfigureAwait(false);
        return new OnlineSpeechResult(result.Text, pcm);
    }

    public async Task<byte[]> SynthesizeEnglishAsync(string englishText, CancellationToken cancellationToken)
    {
        var payload = new { text = englishText, voice = _englishVoice };
        using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync("online-tts", content, cancellationToken).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Edge TTS 失败（HTTP {(int)response.StatusCode}）：{ReadError(json)}");
        using var document = JsonDocument.Parse(json);
        var audioUrl = document.RootElement.GetProperty("audioUrl").GetString();
        if (string.IsNullOrWhiteSpace(audioUrl)) throw new InvalidOperationException("Edge TTS 没有返回音频地址。");
        return await DownloadPcmAsync(audioUrl, cancellationToken).ConfigureAwait(false);
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
        using WaveStream reader = response.Content.Headers.ContentType?.MediaType == "audio/mpeg"
            ? new Mp3FileReader(stream)
            : new WaveFileReader(stream);
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
            provider = "mymemory-edge",
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
            throw new InvalidOperationException($"在线 API 失败（HTTP {(int)response.StatusCode}）：{ReadError(json)}");
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var translated = root.TryGetProperty("translatedText", out var translatedElement)
            ? translatedElement.GetString()?.Trim() : null;
        if (string.IsNullOrWhiteSpace(translated)) throw new InvalidOperationException("在线翻译 API 返回了空译文。");
        var audioUrl = root.TryGetProperty("tts", out var tts) && tts.ValueKind == JsonValueKind.Object
                       && tts.TryGetProperty("audioUrl", out var audio)
            ? audio.GetString()
            : null;
        VoiceScreenLog.Info($"Online API evaluate direction={direction} tts={includeTts}");
        return new EvaluationResult(translated, audioUrl);
    }

    private static string ReadError(string json)
    {
        try { return JsonDocument.Parse(json).RootElement.GetProperty("error").GetString() ?? json; }
        catch { return json; }
    }

    public void Dispose() => _http.Dispose();
    private sealed record EvaluationResult(string Text, string? AudioUrl);
}

public sealed record OnlineSpeechResult(string EnglishText, byte[] Pcm16Mono16Khz);
