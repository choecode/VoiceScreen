using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using VoiceScreen.App.Diagnostics;

namespace VoiceScreen.App.Services;

/// <summary>
/// 双向本地语音翻译：faster-whisper CPU INT8 识别 + OPUS-MT CPU INT8 专用翻译模型。
/// 所有网络请求仅发往 127.0.0.1，不依赖任何云端 API。
/// </summary>
public sealed class LocalOutgoingService : IAsyncDisposable
{
    private const int AsrPort = 18765;
    private static readonly Uri AsrBaseUri = new($"http://127.0.0.1:{AsrPort}/");
    private readonly HttpClient _asr = new() { BaseAddress = AsrBaseUri, Timeout = TimeSpan.FromSeconds(120) };
    private readonly SemaphoreSlim _speechGate = new(1, 1);
    private readonly SemaphoreSlim _translationGate = new(1, 1);
    private Process? _asrProcess;
    private bool _ownsAsr;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await EnsureAsrAsync(cancellationToken).ConfigureAwait(false);
        VoiceScreenLog.Info("Local models ready: faster-whisper base preview + small final + OPUS-MT zh-en/en-zh/th-en CPU INT8");
    }

    public async Task<LocalOutgoingTranslation> TranslateSpeechAsync(byte[] pcm16Mono16Khz, CancellationToken cancellationToken)
    {
        var transcription = await TranscribeWithGateAsync(pcm16Mono16Khz, "zh", cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(transcription.Text))
            throw new InvalidOperationException("本地中文识别没有听清，请再说一次。");
        var english = await TranslateTextWithGateAsync(transcription.Text, TranslationDirection.ChineseToEnglish,
            cancellationToken).ConfigureAwait(false);
        return new LocalOutgoingTranslation(transcription.Text, english);
    }

    public async Task<LocalIncomingTranslation> TranslateIncomingSpeechAsync(byte[] pcm16Mono16Khz,
        CancellationToken cancellationToken)
    {
        var transcription = await TranscribeIncomingSpeechAsync(pcm16Mono16Khz, cancellationToken)
            .ConfigureAwait(false);
        return await TranslateIncomingTextAsync(transcription.Text, transcription.Language, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<LocalTranscription> TranscribeIncomingSpeechAsync(byte[] pcm16Mono16Khz,
        CancellationToken cancellationToken, bool preview = false)
    {
        var transcription = await TranscribeWithGateAsync(pcm16Mono16Khz, "auto", cancellationToken, preview)
            .ConfigureAwait(false);
        VoiceScreenLog.Info($"Incoming ASR language={transcription.Language} text={LogExcerpt(transcription.Text)}");
        return transcription;
    }

    public async Task<LocalIncomingTranslation> TranslateIncomingTextAsync(string text, string detectedLanguage,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new LocalIncomingTranslation(string.Empty, string.Empty, detectedLanguage);
        if (IsLikelyIncomingHallucination(text, detectedLanguage))
        {
            VoiceScreenLog.Warn($"Incoming ASR repetition discarded: {LogExcerpt(text)}");
            return EmptyIncoming(detectedLanguage);
        }
        if (detectedLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase) || ContainsChinese(text))
            return new LocalIncomingTranslation(text.Trim(), text.Trim(), "zh");
        if (detectedLanguage.StartsWith("th", StringComparison.OrdinalIgnoreCase) || ContainsThai(text))
        {
            var englishBridge = await TranslateTextWithGateAsync(text.Trim(), TranslationDirection.ThaiToEnglish,
                cancellationToken).ConfigureAwait(false);
            var thaiChinese = await TranslateTextWithGateAsync(englishBridge, TranslationDirection.EnglishToChinese,
                cancellationToken).ConfigureAwait(false);
            if (IsUnsafeTranslation(text, thaiChinese))
            {
                VoiceScreenLog.Warn($"Pathological Thai translation discarded: {LogExcerpt(thaiChinese)}");
                return EmptyIncoming("th");
            }
            return new LocalIncomingTranslation(text.Trim(), thaiChinese, "th");
        }
        if (!detectedLanguage.StartsWith("en", StringComparison.OrdinalIgnoreCase))
            return new LocalIncomingTranslation(text.Trim(), text.Trim(), detectedLanguage);
        var chinese = await TranslateTextWithGateAsync(text.Trim(), TranslationDirection.EnglishToChinese, cancellationToken)
            .ConfigureAwait(false);
        if (IsUnsafeTranslation(text, chinese))
        {
            VoiceScreenLog.Warn($"Pathological English translation discarded: {LogExcerpt(chinese)}");
            return EmptyIncoming("en");
        }
        return new LocalIncomingTranslation(text.Trim(), chinese, "en");
    }

    private static LocalIncomingTranslation EmptyIncoming(string language)
        => new(string.Empty, string.Empty, language);

    internal static bool IsLikelyIncomingHallucination(string text, string detectedLanguage)
        => IsPathologicalRepetition(text);

    private static bool IsUnsafeTranslation(string source, string translated)
        => translated.Length > Math.Max(120, source.Length * 12)
           || IsPathologicalRepetition(translated);

    private static bool IsPathologicalRepetition(string text)
    {
        var symbols = text.Where(char.IsLetterOrDigit)
            .Select(char.ToUpperInvariant)
            .ToArray();
        if (symbols.Length >= 2)
        {
            var dominantSymbols = symbols.GroupBy(character => character).Max(group => group.Count());
            if ((double)dominantSymbols / symbols.Length >= 0.85)
                return true;
        }

        // 中文没有空格，不能依赖下方的单词计数。检测“我去哪了我去哪了……”这类
        // 短语周期性扩写，同时要求至少约四次重复，避免误杀正常的口语强调。
        if (symbols.Length >= 16 && HasPeriodicSymbolPattern(symbols))
            return true;

        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(word => new string(word.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray()))
            .Where(word => word.Length > 0)
            .ToArray();
        if (words.Length < 6) return false;
        var dominantWords = words.GroupBy(word => word).Max(group => group.Count());
        return (double)dominantWords / words.Length >= 0.7;
    }

    private static bool HasPeriodicSymbolPattern(char[] symbols)
    {
        var maxPeriod = Math.Min(16, symbols.Length / 3);
        for (var period = 2; period <= maxPeriod; period++)
        {
            var matches = 0;
            for (var index = period; index < symbols.Length; index++)
            {
                if (symbols[index] == symbols[index % period]) matches++;
            }

            if ((double)matches / (symbols.Length - period) >= 0.88)
                return true;
        }

        return false;
    }

    private static bool ContainsChinese(string text)
        => text.Any(character => character is >= '\u3400' and <= '\u9fff');

    private static bool ContainsThai(string text)
        => text.Any(character => character is >= '\u0e00' and <= '\u0e7f');

    private static string LogExcerpt(string text)
    {
        var normalized = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return normalized.Length <= 120 ? normalized : normalized[..120] + "…";
    }

    public async Task<string> TranslateChineseTextAsync(string chineseText, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(chineseText)) throw new ArgumentException("请输入要测试的中文。", nameof(chineseText));
        return await TranslateTextWithGateAsync(chineseText.Trim(), TranslationDirection.ChineseToEnglish,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<LocalTranscription> TranscribeWithGateAsync(byte[] pcm16Mono16Khz, string language,
        CancellationToken cancellationToken, bool preview = false)
    {
        await _speechGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await TranscribeAsync(pcm16Mono16Khz, language, cancellationToken, preview).ConfigureAwait(false);
        }
        finally
        {
            _speechGate.Release();
        }
    }

    private async Task<string> TranslateTextWithGateAsync(string source, TranslationDirection direction,
        CancellationToken cancellationToken)
    {
        await _translationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await TranslateTextAsync(source, direction, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _translationGate.Release();
        }
    }

    private async Task<LocalTranscription> TranscribeAsync(byte[] pcm16Mono16Khz, string language,
        CancellationToken cancellationToken, bool preview = false)
    {
        var mode = preview ? "preview" : "final";
        using var response = await PostAudioWithRetryAsync($"transcribe?language={language}&mode={mode}", pcm16Mono16Khz,
            cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"本地语音识别失败：{ReadError(body)}");
        var result = JsonSerializer.Deserialize<TranscriptionResponse>(body, JsonOptions);
        return new LocalTranscription(result?.Text?.Trim() ?? string.Empty, result?.Language?.Trim() ?? string.Empty);
    }

    private async Task<string> TranslateTextAsync(string source, TranslationDirection direction,
        CancellationToken cancellationToken)
    {
        var request = new
        {
            text = source,
            direction = direction switch
            {
                TranslationDirection.ChineseToEnglish => "zh-en",
                TranslationDirection.ThaiToEnglish => "th-en",
                _ => "en-zh"
            }
        };
        var requestJson = JsonSerializer.Serialize(request, JsonOptions);
        using var translationResponse = await PostJsonWithRetryAsync("translate", requestJson, cancellationToken)
            .ConfigureAwait(false);
        var translationBody = await translationResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!translationResponse.IsSuccessStatusCode)
            throw new InvalidOperationException($"本地 OPUS-MT 翻译失败：{ReadError(translationBody)}");
        var translated = JsonSerializer.Deserialize<TranslationResponse>(translationBody, JsonOptions)?.Text?.Trim();
        if (string.IsNullOrWhiteSpace(translated)) throw new InvalidOperationException("本地翻译没有返回结果。");
        return translated;
    }

    private async Task<HttpResponseMessage> PostAudioWithRetryAsync(string path, byte[] audio,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var content = new ByteArrayContent(audio);
                content.Headers.ContentType = new("application/octet-stream");
                return await _asr.PostAsync(path, content, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException) when (attempt == 0 && !cancellationToken.IsCancellationRequested)
            {
                VoiceScreenLog.Warn("Local model audio request disconnected; retrying once");
                await Task.Delay(200, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<HttpResponseMessage> PostJsonWithRetryAsync(string path, string json,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                return await _asr.PostAsync(path, content, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException) when (attempt == 0 && !cancellationToken.IsCancellationRequested)
            {
                VoiceScreenLog.Warn("Local model translation request disconnected; retrying once");
                await Task.Delay(200, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task EnsureAsrAsync(CancellationToken cancellationToken)
    {
        if (await IsHealthyAsync(_asr, "health", cancellationToken).ConfigureAwait(false)) return;
        var script = Path.Combine(AppContext.BaseDirectory, "LocalService", "local_outgoing_service.py");
        if (!File.Exists(script)) throw new FileNotFoundException("缺少本地语音识别服务脚本。", script);
        _asrProcess = StartHiddenProcess("python", $"\"{script}\" --port {AsrPort}");
        _ownsAsr = true;
        // 首次启动会同时把两套 Whisper 和三套 OPUS 模型装入内存；游戏运行时磁盘/CPU
        // 较忙，冷启动可能超过 30 秒。放宽这里只影响启动等待，不影响单次翻译超时。
        await WaitUntilHealthyAsync(_asr, "health", _asrProcess, TimeSpan.FromMinutes(2), cancellationToken,
            "本地模型服务启动失败。请先运行 setup_local_models.ps1，并确认 Python 依赖已安装。")
            .ConfigureAwait(false);
    }

    private static Process StartHiddenProcess(string fileName, string arguments)
    {
        return Process.Start(new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        }) ?? throw new InvalidOperationException($"无法启动本地进程：{fileName}");
    }

    private static async Task WaitUntilHealthyAsync(HttpClient client, string path, Process process, TimeSpan timeout,
        CancellationToken cancellationToken, string failureMessage)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited) throw new InvalidOperationException($"{failureMessage} 进程退出码：{process.ExitCode}");
            if (await IsHealthyAsync(client, path, cancellationToken).ConfigureAwait(false)) return;
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }
        throw new TimeoutException(failureMessage);
    }

    private static async Task<bool> IsHealthyAsync(HttpClient client, string path, CancellationToken cancellationToken)
    {
        try
        {
            using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            requestTimeout.CancelAfter(TimeSpan.FromMilliseconds(500));
            using var response = await client.GetAsync(path, requestTimeout.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static string ReadError(string json)
    {
        try { return JsonDocument.Parse(json).RootElement.GetProperty("error").GetString() ?? json; }
        catch { return json; }
    }

    public ValueTask DisposeAsync()
    {
        _asr.Dispose();
        _speechGate.Dispose();
        _translationGate.Dispose();
        StopOwnedProcess(_asrProcess, _ownsAsr);
        return ValueTask.CompletedTask;
    }

    private static void StopOwnedProcess(Process? process, bool owned)
    {
        if (process is null) return;
        try { if (owned && !process.HasExited) process.Kill(entireProcessTree: true); } catch { }
        process.Dispose();
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record TranscriptionResponse(string Text, string Language);
    private sealed record TranslationResponse(string Text);
    private enum TranslationDirection
    {
        ChineseToEnglish,
        EnglishToChinese,
        ThaiToEnglish
    }
}

public sealed record LocalOutgoingTranslation(string SourceText, string TranslatedText);
public sealed record LocalIncomingTranslation(string SourceText, string TranslatedText, string Language);
public sealed record LocalTranscription(string Text, string Language);
