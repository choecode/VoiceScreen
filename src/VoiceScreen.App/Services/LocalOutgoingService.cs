using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using VoiceScreen.App.Diagnostics;

namespace VoiceScreen.App.Services;

/// <summary>
/// 双向本地语音翻译：faster-whisper CPU INT8 识别 + Ollama qwen2.5:1.5b CPU 翻译。
/// 所有网络请求仅发往 127.0.0.1，不依赖任何云端 API。
/// </summary>
public sealed partial class LocalOutgoingService : IAsyncDisposable
{
    private const int AsrPort = 18765;
    private static readonly Uri AsrBaseUri = new($"http://127.0.0.1:{AsrPort}/");
    private static readonly Uri OllamaBaseUri = new("http://127.0.0.1:11434/");
    private readonly HttpClient _asr = new() { BaseAddress = AsrBaseUri, Timeout = TimeSpan.FromSeconds(30) };
    private readonly HttpClient _ollama = new() { BaseAddress = OllamaBaseUri, Timeout = TimeSpan.FromSeconds(30) };
    private readonly SemaphoreSlim _modelGate = new(1, 1);
    private Process? _asrProcess;
    private Process? _ollamaProcess;
    private bool _ownsAsr;
    private bool _ownsOllama;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await EnsureOllamaAsync(cancellationToken).ConfigureAwait(false);
        await EnsureAsrAsync(cancellationToken).ConfigureAwait(false);
        VoiceScreenLog.Info("Local bidirectional service ready: faster-whisper small CPU INT8 + qwen2.5:1.5b CPU");
    }

    public async Task<LocalOutgoingTranslation> TranslateSpeechAsync(byte[] pcm16Mono16Khz, CancellationToken cancellationToken)
    {
        await _modelGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var chinese = await TranscribeAsync(pcm16Mono16Khz, "zh", cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(chinese)) throw new InvalidOperationException("本地中文识别没有听清，请再说一次。");
            var english = await TranslateTextAsync(chinese, TranslationDirection.ChineseToEnglish, cancellationToken)
                .ConfigureAwait(false);
            return new LocalOutgoingTranslation(chinese, english);
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
            var english = await TranscribeAsync(pcm16Mono16Khz, "en", cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(english)) return new LocalIncomingTranslation(string.Empty, string.Empty);
            var chinese = await TranslateTextAsync(english, TranslationDirection.EnglishToChinese, cancellationToken)
                .ConfigureAwait(false);
            return new LocalIncomingTranslation(english, chinese);
        }
        finally
        {
            _modelGate.Release();
        }
    }

    private async Task<string> TranscribeAsync(byte[] pcm16Mono16Khz, string language,
        CancellationToken cancellationToken)
    {
        using var audioContent = new ByteArrayContent(pcm16Mono16Khz);
        audioContent.Headers.ContentType = new("application/octet-stream");
        using var response = await _asr.PostAsync($"transcribe?language={language}", audioContent, cancellationToken)
            .ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"本地语音识别失败：{ReadError(body)}");
        return JsonSerializer.Deserialize<TranscriptionResponse>(body, JsonOptions)?.Text?.Trim() ?? string.Empty;
    }

    private async Task<string> TranslateTextAsync(string source, TranslationDirection direction,
        CancellationToken cancellationToken)
    {
        var systemPrompt = direction == TranslationDirection.ChineseToEnglish
            ? "You are a game voice translator. Translate Chinese faithfully into concise natural spoken English. Preserve tactical details, numbers, floor numbers, directions, and locations exactly. Output only the English translation, with no explanation, labels, quotes, or alternatives."
            : "你是游戏语音翻译。把英文忠实翻译成简洁自然的简体中文，准确保留战术细节、数字、楼层、方向和位置。只输出中文译文，不要解释、标签、引号或备选译法。";
        var request = new
        {
            model = "qwen2.5:1.5b",
            stream = false,
            keep_alive = "30m",
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = source }
            },
            options = new { temperature = 0, num_gpu = 0, num_predict = 128 }
        };
        using var translationResponse = await _ollama.PostAsJsonAsync("api/chat", request, JsonOptions, cancellationToken).ConfigureAwait(false);
        var translationBody = await translationResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!translationResponse.IsSuccessStatusCode)
            throw new InvalidOperationException($"本地中译英失败：{ReadError(translationBody)}");
        var ollama = JsonSerializer.Deserialize<OllamaResponse>(translationBody, JsonOptions);
        var translated = CleanTranslation(ollama?.Message?.Content);
        if (string.IsNullOrWhiteSpace(translated)) throw new InvalidOperationException("本地翻译没有返回结果。");
        return translated;
    }

    private async Task EnsureAsrAsync(CancellationToken cancellationToken)
    {
        if (await IsHealthyAsync(_asr, "health", cancellationToken).ConfigureAwait(false)) return;
        var script = Path.Combine(AppContext.BaseDirectory, "LocalService", "local_outgoing_service.py");
        if (!File.Exists(script)) throw new FileNotFoundException("缺少本地语音识别服务脚本。", script);
        _asrProcess = StartHiddenProcess("python", $"\"{script}\" --port {AsrPort}");
        _ownsAsr = true;
        await WaitUntilHealthyAsync(_asr, "health", _asrProcess, TimeSpan.FromSeconds(30), cancellationToken,
            "本地语音识别服务启动失败。请确认 Python 3.11 和 faster-whisper 已安装。").ConfigureAwait(false);
    }

    private async Task EnsureOllamaAsync(CancellationToken cancellationToken)
    {
        if (!await IsHealthyAsync(_ollama, "api/tags", cancellationToken).ConfigureAwait(false))
        {
            _ollamaProcess = StartHiddenProcess("ollama", "serve");
            _ownsOllama = true;
            await WaitUntilHealthyAsync(_ollama, "api/tags", _ollamaProcess, TimeSpan.FromSeconds(15), cancellationToken,
                "Ollama 本地服务启动失败。").ConfigureAwait(false);
        }

        using var tagsResponse = await _ollama.GetAsync("api/tags", cancellationToken).ConfigureAwait(false);
        var tags = await tagsResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!tags.Contains("qwen2.5:1.5b", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("本机缺少 Ollama 模型 qwen2.5:1.5b，请先执行：ollama pull qwen2.5:1.5b");

        // 启动阶段预热模型，避免用户第一次松开右 Alt 时才等待模型载入内存。
        var warmup = new
        {
            model = "qwen2.5:1.5b",
            stream = false,
            keep_alive = "30m",
            prompt = "Translate to English, output only the translation: 你好",
            options = new { temperature = 0, num_gpu = 0, num_predict = 16 }
        };
        using var warmupResponse = await _ollama.PostAsJsonAsync("api/generate", warmup, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
        if (!warmupResponse.IsSuccessStatusCode)
            throw new InvalidOperationException("Ollama qwen2.5:1.5b 本地模型预热失败。");
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

    private static string CleanTranslation(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var cleaned = ThinkBlockRegex().Replace(text, string.Empty).Trim();
        cleaned = cleaned.Trim('"', '\'', '“', '”');
        foreach (var prefix in new[] { "Translation:", "English:", "英文：", "翻译：" })
            if (cleaned.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) cleaned = cleaned[prefix.Length..].Trim();
        return cleaned;
    }

    public ValueTask DisposeAsync()
    {
        _asr.Dispose();
        _ollama.Dispose();
        _modelGate.Dispose();
        StopOwnedProcess(_asrProcess, _ownsAsr);
        StopOwnedProcess(_ollamaProcess, _ownsOllama);
        return ValueTask.CompletedTask;
    }

    private static void StopOwnedProcess(Process? process, bool owned)
    {
        if (process is null) return;
        try { if (owned && !process.HasExited) process.Kill(entireProcessTree: true); } catch { }
        process.Dispose();
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [GeneratedRegex("<think>[\\s\\S]*?</think>", RegexOptions.IgnoreCase)]
    private static partial Regex ThinkBlockRegex();

    private sealed record TranscriptionResponse(string Text, string Language);
    private sealed record OllamaResponse(OllamaMessage? Message);
    private sealed record OllamaMessage(string? Content);

    private enum TranslationDirection
    {
        ChineseToEnglish,
        EnglishToChinese
    }
}

public sealed record LocalOutgoingTranslation(string SourceText, string TranslatedText);
public sealed record LocalIncomingTranslation(string SourceText, string TranslatedText);
