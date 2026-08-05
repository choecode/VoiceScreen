using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using NAudio.Wave;
using VoiceScreen.App.Diagnostics;
using VoiceScreen.Core;

namespace VoiceScreen.App.Services;

/// <summary>
/// 通过本机助手进程代理无密钥的 MyMemory 翻译与 Edge TTS；语音识别始终在本机完成。
/// 走的是和本地模型同一个 <see cref="LocalOutgoingService.ServicePort"/> 服务，
/// 只是请求里选了 online 提供方。
/// </summary>
public sealed class OnlineApiService : IDisposable
{
    private const string DefaultEnglishVoice = "en-US-JennyNeural";

    private readonly HttpClient _http;
    private readonly string _englishVoice;

    public OnlineApiService(string? englishVoice = null)
    {
        _http = new HttpClient
        {
            BaseAddress = new Uri($"http://127.0.0.1:{LocalOutgoingService.ServicePort}/"),
            Timeout = TimeSpan.FromSeconds(60)
        };
        _englishVoice = string.IsNullOrWhiteSpace(englishVoice) ? DefaultEnglishVoice : englishVoice.Trim();
    }

    public async Task<string> TestAsync(CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        var healthBody = await CheckHealthAsync(cancellationToken).ConfigureAwait(false);
        var english = await TranslateChineseAsync("敌人在二楼，我们从左边走。", cancellationToken).ConfigureAwait(false);
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
        => (await TranslateAsync(text, TranslationDirection.ChineseToEnglish, false, cancellationToken)
            .ConfigureAwait(false)).Text;

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
        // 语种判定和本地链路共用 SpokenLanguage，避免两条链路对同一句话给出不同语种。
        var detected = SpokenLanguage.Detect(text, language);
        if (detected == SpokenLanguage.Chinese)
            return new LocalIncomingTranslation(text.Trim(), text.Trim(), SpokenLanguage.Chinese);

        var direction = detected == SpokenLanguage.Thai
            ? TranslationDirection.ThaiToChinese
            : TranslationDirection.EnglishToChinese;
        var translated = await TranslateAsync(text, direction, false, cancellationToken).ConfigureAwait(false);
        return new LocalIncomingTranslation(text.Trim(), translated.Text,
            direction == TranslationDirection.ThaiToChinese ? SpokenLanguage.Thai : SpokenLanguage.English);
    }

    private async Task<byte[]> DownloadPcmAsync(string audioUrl, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(audioUrl.TrimStart('/'), cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var waveBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        using var stream = new MemoryStream(waveBytes, writable: false);
        using WaveStream reader = response.Content.Headers.ContentType?.MediaType == "audio/mpeg"
            ? new Mp3FileReader(stream)
            : new WaveFileReader(stream);
        using var resampler = new MediaFoundationResampler(reader, new WaveFormat(16000, 16, 1))
        {
            ResamplerQuality = 60
        };
        using var pcm = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = resampler.Read(buffer, 0, buffer.Length)) > 0) pcm.Write(buffer, 0, read);
        return pcm.ToArray();
    }

    private async Task<EvaluationResult> TranslateAsync(string text, TranslationDirection direction, bool includeTts,
        CancellationToken cancellationToken)
    {
        var payload = new
        {
            provider = "mymemory-edge",
            text,
            direction = direction.ToWireValue(),
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
            ? translatedElement.GetString()?.Trim()
            : null;
        if (string.IsNullOrWhiteSpace(translated)) throw new InvalidOperationException("在线翻译 API 返回了空译文。");
        var audioUrl = root.TryGetProperty("tts", out var tts) && tts.ValueKind == JsonValueKind.Object
                       && tts.TryGetProperty("audioUrl", out var audio)
            ? audio.GetString()
            : null;
        VoiceScreenLog.Info($"Online API evaluate direction={direction.ToWireValue()} tts={includeTts}");
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
