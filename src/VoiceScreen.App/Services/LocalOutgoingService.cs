using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using VoiceScreen.App.Diagnostics;
using VoiceScreen.Core;

namespace VoiceScreen.App.Services;

/// <summary>
/// 双向本地语音翻译：faster-whisper CPU INT8 识别 + OPUS-MT CPU INT8 专用翻译模型。
/// 所有网络请求仅发往 127.0.0.1，不依赖任何云端 API。
/// </summary>
public sealed class LocalOutgoingService : IAsyncDisposable
{
    /// <summary>
    /// 本地推理服务端口。这是全项目唯一定义处——之前 OnlineApiService 里还硬编码了
    /// 一份 18765，两边分头改就会连到不存在的服务上。
    /// </summary>
    public const int ServicePort = 18765;

    private readonly HttpClient _asr;
    private readonly SemaphoreSlim _speechGate = new(1, 1);
    private readonly SemaphoreSlim _translationGate = new(1, 1);
    private readonly ChildProcessJob? _processJob = ChildProcessJob.TryCreate();
    private readonly string _asrEngine;
    private Process? _asrProcess;
    private bool _ownsAsr;

    public LocalOutgoingService(string asrEngine = "whisper")
    {
        _asrEngine = NormalizeAsrEngine(asrEngine);
        _asr = new HttpClient
        {
            BaseAddress = new Uri($"http://127.0.0.1:{ServicePort}/"),
            Timeout = TimeSpan.FromSeconds(120)
        };
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await EnsureAsrAsync(cancellationToken).ConfigureAwait(false);
        var asrModelLabel = _asrEngine == "sherpa" ? "Sherpa-ONNX Zipformer" : "faster-whisper (base/small)";
        VoiceScreenLog.Info(
            $"Local models ready on {ServicePort}: {asrModelLabel} + OPUS-MT zh-en/en-zh/th-en CPU INT8");
    }

    public async Task<LocalTranscription> TranscribeChineseSpeechAsync(ReadOnlyMemory<byte> pcm16Mono16Khz,
        CancellationToken cancellationToken)
        => await TranscribeWithGateAsync(pcm16Mono16Khz, SpokenLanguage.Chinese, cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// 接收 <see cref="ReadOnlyMemory{T}"/> 而不是 byte[]，调用方可以直接传入
    /// ArrayPool 租来的缓冲的一段，不必为每个音频快照单独分配精确长度的数组。
    /// </summary>
    public async Task<LocalTranscription> TranscribeIncomingSpeechAsync(ReadOnlyMemory<byte> pcm16Mono16Khz,
        CancellationToken cancellationToken, bool preview = false)
    {
        var transcription = await TranscribeWithGateAsync(pcm16Mono16Khz, SpokenLanguage.Unknown, cancellationToken,
            preview).ConfigureAwait(false);
        VoiceScreenLog.Info($"Incoming ASR language={transcription.Language} text={LogExcerpt(transcription.Text)}");
        return transcription;
    }

    public async Task<LocalIncomingTranslation> TranslateIncomingTextAsync(string text, string detectedLanguage,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new LocalIncomingTranslation(string.Empty, string.Empty, detectedLanguage);
        if (TranscriptSanitizer.IsPathologicalRepetition(text))
        {
            VoiceScreenLog.Warn($"Incoming ASR repetition discarded: {LogExcerpt(text)}");
            return EmptyIncoming(detectedLanguage);
        }

        // 文本字符优先于 ASR 报告的语种标签——Whisper 在短句上的 language 字段并不可靠。
        var language = SpokenLanguage.Detect(text, detectedLanguage);

        // 中文本身就是字幕的目标语言，原样上屏。
        if (language == SpokenLanguage.Chinese)
            return new LocalIncomingTranslation(text.Trim(), text.Trim(), SpokenLanguage.Chinese);

        // 中英泰以外的语种没有可用模型，透传原文，不要凭空造译文。
        if (language != SpokenLanguage.English && language != SpokenLanguage.Thai)
            return new LocalIncomingTranslation(text.Trim(), text.Trim(), language);

        var direction = language == SpokenLanguage.Thai
            ? TranslationDirection.ThaiToChinese
            : TranslationDirection.EnglishToChinese;

        var chinese = await TranslateThroughModelPairAsync(text.Trim(), direction, cancellationToken)
            .ConfigureAwait(false);
        if (TranscriptSanitizer.IsUnsafeTranslation(text, chinese))
        {
            VoiceScreenLog.Warn($"Pathological {language} translation discarded: {LogExcerpt(chinese)}");
            return EmptyIncoming(language);
        }

        return new LocalIncomingTranslation(text.Trim(), chinese, language);
    }

    public async Task<string> TranslateChineseTextAsync(string chineseText, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(chineseText))
            throw new ArgumentException("请输入要测试的中文。", nameof(chineseText));
        return await TranslateThroughModelPairAsync(chineseText.Trim(), TranslationDirection.ChineseToEnglish,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 按 <see cref="TranslationDirections.ToModelPair"/> 给出的模型对逐段翻译。
    /// 泰译中没有直接模型，会自动走 th-en -> en-zh 的英文桥接；桥接这件事在这里是
    /// 数据驱动的，不再是散落在各调用点的 if 分支。
    /// </summary>
    private async Task<string> TranslateThroughModelPairAsync(string text, TranslationDirection direction,
        CancellationToken cancellationToken)
    {
        var current = text;
        foreach (var modelPair in direction.ToModelPair())
            current = await TranslateTextWithGateAsync(current, modelPair, cancellationToken).ConfigureAwait(false);
        return current;
    }

    private static LocalIncomingTranslation EmptyIncoming(string language)
        => new(string.Empty, string.Empty, language);

    private static string LogExcerpt(string text)
    {
        var normalized = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return normalized.Length <= 120 ? normalized : normalized[..120] + "…";
    }

    private async Task<LocalTranscription> TranscribeWithGateAsync(ReadOnlyMemory<byte> pcm16Mono16Khz, string language,
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

    private async Task<string> TranslateTextWithGateAsync(string source, string modelPair,
        CancellationToken cancellationToken)
    {
        await _translationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await TranslateTextAsync(source, modelPair, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _translationGate.Release();
        }
    }

    private async Task<LocalTranscription> TranscribeAsync(ReadOnlyMemory<byte> pcm16Mono16Khz, string language,
        CancellationToken cancellationToken, bool preview = false)
    {
        var mode = preview ? "preview" : "final";
        using var response = await PostAudioWithRetryAsync($"transcribe?language={language}&mode={mode}",
            pcm16Mono16Khz, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"本地语音识别失败：{ReadError(body)}");
        var result = JsonSerializer.Deserialize<TranscriptionResponse>(body, JsonOptions);
        return new LocalTranscription(result?.Text?.Trim() ?? string.Empty, result?.Language?.Trim() ?? string.Empty);
    }

    private async Task<string> TranslateTextAsync(string source, string modelPair,
        CancellationToken cancellationToken)
    {
        var requestJson = JsonSerializer.Serialize(new { text = source, direction = modelPair }, JsonOptions);
        using var translationResponse = await PostJsonWithRetryAsync("translate", requestJson, cancellationToken)
            .ConfigureAwait(false);
        var translationBody = await translationResponse.Content.ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!translationResponse.IsSuccessStatusCode)
            throw new InvalidOperationException($"本地 OPUS-MT 翻译失败：{ReadError(translationBody)}");
        var translated = JsonSerializer.Deserialize<TranslationResponse>(translationBody, JsonOptions)?.Text?.Trim();
        if (string.IsNullOrWhiteSpace(translated))
            throw new InvalidOperationException("本地翻译没有返回结果。");
        return translated;
    }

    private async Task<HttpResponseMessage> PostAudioWithRetryAsync(string path, ReadOnlyMemory<byte> audio,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var content = new ReadOnlyMemoryContent(audio);
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
        var arguments = $"\"{script}\" --port {ServicePort} --asr-engine {_asrEngine}";
        _asrProcess = StartHiddenProcess("python", arguments);
        _ownsAsr = true;

        // 挂进 kill-on-close job：即使本进程崩溃或被强杀，内核也会连带回收这个
        // Python 服务，不会留下占着模型内存和 18765 端口的孤儿进程。
        _processJob?.Assign(_asrProcess);

        // 首次启动会同时把两套 Whisper 和三套 OPUS 模型装入内存；游戏运行时磁盘/CPU
        // 较忙，冷启动可能超过 30 秒。放宽这里只影响启动等待，不影响单次翻译超时。
        await WaitUntilHealthyAsync(_asr, "health", _asrProcess, TimeSpan.FromMinutes(2), cancellationToken,
            "本地模型服务启动失败。请先运行 setup_local_models.ps1，并确认 Python 依赖已安装。")
            .ConfigureAwait(false);
    }

    private async Task WaitUntilHealthyAsync(HttpClient client, string path, Process process, TimeSpan timeout,
        CancellationToken cancellationToken, string failureMessage)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited)
            {
                // 给 stderr 的异步回调一点时间把最后几行读完，否则常常只剩空信息。
                await Task.Delay(150, CancellationToken.None).ConfigureAwait(false);
                throw new InvalidOperationException(DescribeStartupFailure(failureMessage, process));
            }
            if (await IsHealthyAsync(client, path, cancellationToken).ConfigureAwait(false)) return;
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }
        throw new TimeoutException(failureMessage);
    }

    /// <summary>
    /// 启动隐藏的子进程，并把它的 stderr 收集起来。
    ///
    /// 之前这里不接管 stderr，Python 的 traceback 直接丢进虚空，用户只能看到
    /// "本地模型服务启动失败，请先运行 setup_local_models.ps1" 这句放之四海而皆准的话，
    /// 而真正的原因（缺哪个包、少哪个模型文件）完全不可见。
    /// </summary>
    private Process StartHiddenProcess(string fileName, string arguments)
    {
        var process = Process.Start(new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardError = true
        }) ?? throw new InvalidOperationException($"无法启动本地进程：{fileName}");

        process.ErrorDataReceived += (_, args) =>
        {
            if (string.IsNullOrWhiteSpace(args.Data)) return;
            VoiceScreenLog.Warn($"[local service] {args.Data}");
            lock (_startupErrors)
            {
                // 只留最后若干行：Python traceback 的最后一行才是真正的异常。
                _startupErrors.Enqueue(args.Data.Trim());
                while (_startupErrors.Count > MaxStartupErrorLines) _startupErrors.Dequeue();
            }
        };
        process.BeginErrorReadLine();
        return process;
    }

    private const int MaxStartupErrorLines = 6;
    private readonly Queue<string> _startupErrors = new();

    /// <summary>把子进程 stderr 的最后几行拼进错误信息，让用户直接看到根因。</summary>
    private string DescribeStartupFailure(string baseMessage, Process process)
    {
        string[] lines;
        lock (_startupErrors) lines = [.. _startupErrors];
        if (lines.Length == 0) return $"{baseMessage} 进程退出码：{process.ExitCode}";

        // ModuleNotFoundError 是最常见的一类，单独给一句能直接照做的提示。
        var last = lines[^1];
        var hint = last.Contains("ModuleNotFoundError", StringComparison.Ordinal)
            ? "\n缺少 Python 依赖，请先安装它，或在主界面把对应引擎切回默认选项。"
            : string.Empty;

        return $"{baseMessage} 进程退出码：{process.ExitCode}{hint}\n本地服务输出：\n{string.Join('\n', lines)}";
    }

    private static string NormalizeAsrEngine(string asrEngine) => asrEngine switch
    {
        "sherpa" or "sherpa-onnx" or "zipformer" => "sherpa",
        _ => "whisper"
    };

    /// <summary>ASR 和翻译两套模型都装载完成才算就绪，否则第一句话必然失败。</summary>
    private static async Task<bool> IsHealthyAsync(HttpClient client, string path,
        CancellationToken cancellationToken)
    {
        try
        {
            using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            requestTimeout.CancelAfter(TimeSpan.FromMilliseconds(500));
            using var response = await client.GetAsync(path, requestTimeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return false;
            var json = await response.Content.ReadAsStringAsync(requestTimeout.Token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            return IsComponentReady(root, "asr") && IsComponentReady(root, "translation");
        }
        catch
        {
            return false;
        }
    }

    private static bool IsComponentReady(JsonElement root, string property)
        => root.TryGetProperty(property, out var value)
           && !string.Equals(value.GetString(), "disabled", StringComparison.OrdinalIgnoreCase);

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
        // job 句柄最后关：句柄一关，内核就回收 job 里所有还活着的进程。
        _processJob?.Dispose();
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
}

public sealed record LocalOutgoingTranslation(string SourceText, string TranslatedText);
public sealed record LocalIncomingTranslation(string SourceText, string TranslatedText, string Language);
public sealed record LocalTranscription(string Text, string Language);
