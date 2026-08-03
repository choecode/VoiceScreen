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
    private readonly SemaphoreSlim _modelGate = new(1, 1);
    private Process? _asrProcess;
    private bool _ownsAsr;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await EnsureAsrAsync(cancellationToken).ConfigureAwait(false);
        VoiceScreenLog.Info("Local models ready: faster-whisper small + OPUS-MT zh-en/en-zh/th-en CPU INT8");
    }

    public async Task<LocalOutgoingTranslation> TranslateSpeechAsync(byte[] pcm16Mono16Khz, CancellationToken cancellationToken)
    {
        await _modelGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var transcription = await TranscribeAsync(pcm16Mono16Khz, "zh", cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(transcription.Text)) throw new InvalidOperationException("本地中文识别没有听清，请再说一次。");
            var english = await TranslateTextAsync(transcription.Text, TranslationDirection.ChineseToEnglish, cancellationToken)
                .ConfigureAwait(false);
            return new LocalOutgoingTranslation(transcription.Text, english);
        }
        finally
        {
            _modelGate.Release();
        }
    }

    public async Task<LocalIncomingTranslation> TranslateIncomingSpeechAsync(byte[] pcm16Mono16Khz,
        CancellationToken cancellationToken)
    {
        await _modelGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var transcription = await TranscribeAsync(pcm16Mono16Khz, "auto", cancellationToken).ConfigureAwait(false);
            return await TranslateIncomingTextAsync(transcription.Text, transcription.Language, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _modelGate.Release();
        }
    }

    public async Task<LocalIncomingTranslation> TranslateIncomingTextAsync(string text, string detectedLanguage,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new LocalIncomingTranslation(string.Empty, string.Empty, detectedLanguage);
        if (detectedLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase) || ContainsChinese(text))
            return new LocalIncomingTranslation(text.Trim(), text.Trim(), "zh");
        if (!detectedLanguage.StartsWith("en", StringComparison.OrdinalIgnoreCase))
        {
            if (!detectedLanguage.StartsWith("th", StringComparison.OrdinalIgnoreCase))
                return new LocalIncomingTranslation(text.Trim(), text.Trim(), detectedLanguage);
            var englishBridge = await TranslateTextAsync(text.Trim(), TranslationDirection.ThaiToEnglish,
                cancellationToken).ConfigureAwait(false);
            var thaiChinese = await TranslateTextAsync(englishBridge, TranslationDirection.EnglishToChinese,
                cancellationToken).ConfigureAwait(false);
            return new LocalIncomingTranslation(text.Trim(), thaiChinese, "th");
        }
        var chinese = await TranslateTextAsync(text.Trim(), TranslationDirection.EnglishToChinese, cancellationToken)
            .ConfigureAwait(false);
        return new LocalIncomingTranslation(text.Trim(), chinese, "en");
    }

    private static bool ContainsChinese(string text)
        => text.Any(character => character is >= '\u3400' and <= '\u9fff');

    public async Task<string> TranslateChineseTextAsync(string chineseText, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(chineseText)) throw new ArgumentException("请输入要测试的中文。", nameof(chineseText));
        await _modelGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await TranslateTextAsync(chineseText.Trim(), TranslationDirection.ChineseToEnglish, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _modelGate.Release();
        }
    }

    private async Task<LocalTranscription> TranscribeAsync(byte[] pcm16Mono16Khz, string language,
        CancellationToken cancellationToken)
    {
        using var response = await PostAudioWithRetryAsync($"transcribe?language={language}", pcm16Mono16Khz,
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
        await WaitUntilHealthyAsync(_asr, "health", _asrProcess, TimeSpan.FromSeconds(30), cancellationToken,
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
        _modelGate.Dispose();
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
    private sealed record LocalTranscription(string Text, string Language);

    private enum TranslationDirection
    {
        ChineseToEnglish,
        EnglishToChinese,
        ThaiToEnglish
    }
}

public sealed record LocalOutgoingTranslation(string SourceText, string TranslatedText);
public sealed record LocalIncomingTranslation(string SourceText, string TranslatedText, string Language);
