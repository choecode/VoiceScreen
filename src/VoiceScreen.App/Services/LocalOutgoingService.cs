using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using VoiceScreen.App.Diagnostics;
using VoiceScreen.Core;

namespace VoiceScreen.App.Services;

/// <summary>
/// 双向语音翻译模型客户端：可连接 Spark 上的 Qwen 模型服务，也可启动本机
/// faster-whisper + OPUS-MT 备用服务。
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
    private readonly string _asrDevice;
    private readonly bool _remoteMode;
    private Process? _asrProcess;
    private bool _ownsAsr;
    private bool _qwenStreamingAvailable;

    public LocalOutgoingService(
        string asrEngine = "whisper",
        string asrDevice = "auto",
        string? modelServiceUrl = null,
        string? modelServiceToken = null)
    {
        _asrEngine = NormalizeAsrEngine(asrEngine);
        _asrDevice = NormalizeAsrDevice(asrDevice);
        _remoteMode = _asrEngine == "qwen3-asr";

        // 低延迟模式每 600ms 就要发一次识别请求，中间还夹着翻译请求。默认的
        // HttpClient 会周期性回收连接，每次回收都让下一个快照多付一次 TCP 握手。
        // 这里让连接常驻：目标是 127.0.0.1 上的固定进程，没有 DNS 变更或负载均衡
        // 需要靠回收来跟进；服务端同时声明了 HTTP/1.1 keep-alive。
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = Timeout.InfiniteTimeSpan,
            PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan,
            // ASR 和翻译各占一条，剩下的留给发送方向的抢跑识别。
            MaxConnectionsPerServer = 4,
            UseProxy = false,
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(5)
        };
        var baseAddress = _remoteMode
            ? ValidateServiceUrl(modelServiceUrl)
            : new Uri($"http://127.0.0.1:{ServicePort}/");
        _asr = new HttpClient(handler)
        {
            BaseAddress = baseAddress,
            Timeout = TimeSpan.FromSeconds(120),
            DefaultRequestVersion = System.Net.HttpVersion.Version11,
            DefaultVersionPolicy = System.Net.Http.HttpVersionPolicy.RequestVersionOrLower
        };
        if (_remoteMode)
        {
            if (string.IsNullOrWhiteSpace(modelServiceToken))
                throw new InvalidOperationException("Spark 模型服务访问令牌为空，请在主界面填写访问令牌。");
            _asr.DefaultRequestHeaders.TryAddWithoutValidation("X-VoiceScreen-Token", modelServiceToken.Trim());
        }
    }

    /// <summary>
    /// 服务端实际用上的 Whisper 设备（cuda 或 cpu）。请求的是 auto 时，只有服务起来
    /// 之后才知道显卡到底能不能用，界面要显示真实结果而不是我们的意愿。
    /// </summary>
    public string ActiveAsrDevice { get; private set; } = "cpu";

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await EnsureAsrAsync(cancellationToken).ConfigureAwait(false);
        _qwenStreamingAvailable = await ReadQwenStreamingAvailabilityAsync(cancellationToken).ConfigureAwait(false);
        ActiveAsrDevice = await ReadActiveAsrDeviceAsync(cancellationToken).ConfigureAwait(false);
        var asrModelLabel = _asrEngine == "qwen3-asr"
            ? _qwenStreamingAvailable
                ? $"Qwen3-ASR-1.7B realtime on {ActiveAsrDevice.ToUpperInvariant()}"
                : $"Qwen3-ASR-1.7B offline fallback on {ActiveAsrDevice.ToUpperInvariant()}"
            : _asrEngine == "sherpa"
            ? "Sherpa-ONNX Zipformer (streaming)"
            : $"faster-whisper (base/small) on {ActiveAsrDevice.ToUpperInvariant()}";
        VoiceScreenLog.Info(
            $"Model service ready at {_asr.BaseAddress}: {asrModelLabel} + translation");
        if (_remoteMode && !_qwenStreamingAvailable)
            VoiceScreenLog.Warn("Spark service does not advertise Qwen streaming; using stateless ASR fallback");
    }

    private async Task<string> ReadActiveAsrDeviceAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _asr.GetAsync("health", cancellationToken).ConfigureAwait(false);
            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("asrDevice", out var value)
                ? value.GetString() ?? "cpu"
                : "cpu";
        }
        catch
        {
            // 只是一条展示信息，拿不到就当 CPU，不能因此让启动失败。
            return "cpu";
        }
    }

    public async Task<LocalTranscription> TranscribeChineseSpeechAsync(ReadOnlyMemory<byte> pcm16Mono16Khz,
        CancellationToken cancellationToken)
        => await TranscribeChineseSpeechAsync(pcm16Mono16Khz,
            new TranscriptionRequest { Language = SpokenLanguage.Chinese }, cancellationToken).ConfigureAwait(false);

    public async Task<LocalTranscription> TranscribeChineseSpeechAsync(ReadOnlyMemory<byte> pcm16Mono16Khz,
        TranscriptionRequest request, CancellationToken cancellationToken)
        => await TranscribeWithGateAsync(pcm16Mono16Khz, request with { Language = SpokenLanguage.Chinese },
            cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// 接收 <see cref="ReadOnlyMemory{T}"/> 而不是 byte[]，调用方可以直接传入
    /// ArrayPool 租来的缓冲的一段，不必为每个音频快照单独分配精确长度的数组。
    /// </summary>
    public async Task<LocalTranscription> TranscribeIncomingSpeechAsync(ReadOnlyMemory<byte> pcm16Mono16Khz,
        TranscriptionRequest request, CancellationToken cancellationToken)
    {
        // Spark 接收链路服务于英文视频/语音。短促的 "uh huh" 在仅有一秒上下文时偶尔会
        // 被自动语言识别判成中文“嗯”；给 Qwen 明确 English 提示可消除这次无谓回退。
        // 麦克风发送方向走 TranscribeChineseSpeechAsync，仍然固定为 Chinese。
        var languageHint = _asrEngine == "qwen3-asr" ? SpokenLanguage.English : SpokenLanguage.Unknown;
        var transcription = await TranscribeWithGateAsync(pcm16Mono16Khz,
            request with { Language = languageHint }, cancellationToken).ConfigureAwait(false);
        // 正常日志不记录用户听到的原始语音内容；冒烟模式会在自己的事件处理器里记录测试文本。
        VoiceScreenLog.Info(
            $"Incoming ASR language={transcription.Language} chars={transcription.Text.Length} words={transcription.Words?.Count ?? 0}");
        return transcription;
    }

    /// <summary>识别引擎是否天然按增量音频工作。</summary>
    /// <remarks>
    /// Sherpa 和 Spark Qwen 会话都把解码状态留在服务端，客户端每次只送新到的
    /// 那几百毫秒；Whisper 无状态，只能每次重送整个滚动窗口，靠词级时间戳裁掉已确认
    /// 的部分来控制窗口长度。两条路的音频送法完全相反，调用方必须能区分。
    /// </remarks>
    public bool UsesStreamingSessions => _asrEngine == "sherpa" || _qwenStreamingAvailable;

    /// <summary>
    /// 无状态滚动窗口只有拿到词级时间戳才能安全提交文本并裁掉对应音频。
    /// 当前本机 Whisper 服务支持；Spark 上的 Qwen3-ASR 只返回整段文本。
    /// </summary>
    public bool SupportsWordTimestamps => _asrEngine == "whisper";

    /// <summary>Spark 的常驻 Qwen3-4B 可做受约束的字幕边界判断，无需再下载模型。</summary>
    public bool SupportsSemanticSegmentation => _remoteMode;

    /// <summary>
    /// 临时字幕用的束宽。临时结果注定会被下一次识别覆盖，为它跑四路束搜索是纯浪费；
    /// 贪心解码在同一个 OPUS-MT 模型上通常快一倍以上，质量差异在最终定稿时会被修正。
    /// </summary>
    public const int PartialBeamSize = 1;

    /// <summary>最终定稿用的束宽，保持一期的翻译质量。</summary>
    public const int FinalBeamSize = 4;

    public async Task<LocalIncomingTranslation> TranslateIncomingTextAsync(string text, string detectedLanguage,
        CancellationToken cancellationToken)
        => await TranslateIncomingTextAsync(text, detectedLanguage, FinalBeamSize, cancellationToken)
            .ConfigureAwait(false);

    public async Task<LocalIncomingTranslation> TranslateIncomingTextAsync(string text, string detectedLanguage,
        int beamSize, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new LocalIncomingTranslation(string.Empty, string.Empty, detectedLanguage);
        if (TranscriptSanitizer.IsPathologicalRepetition(text))
        {
            VoiceScreenLog.Warn($"Incoming ASR repetition discarded: chars={text.Length}");
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

        var chinese = await TranslateThroughModelPairAsync(text.Trim(), direction, beamSize, cancellationToken)
            .ConfigureAwait(false);
        if (TranscriptSanitizer.IsUnsafeTranslation(text, chinese))
        {
            VoiceScreenLog.Warn($"Pathological {language} translation discarded: chars={chinese.Length}");
            return EmptyIncoming(language);
        }

        return new LocalIncomingTranslation(text.Trim(), chinese, language);
    }

    public async Task<string> TranslateChineseTextAsync(string chineseText, CancellationToken cancellationToken)
        => await TranslateChineseTextAsync(chineseText, FinalBeamSize, cancellationToken).ConfigureAwait(false);

    public async Task<string> TranslateChineseTextAsync(string chineseText, int beamSize,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(chineseText))
            throw new ArgumentException("请输入要测试的中文。", nameof(chineseText));
        return await TranslateThroughModelPairAsync(chineseText.Trim(), TranslationDirection.ChineseToEnglish,
            beamSize, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> ShouldBreakSubtitleAsync(string text, CancellationToken cancellationToken)
    {
        if (!SupportsSemanticSegmentation || string.IsNullOrWhiteSpace(text)) return false;
        await _translationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var requestJson = JsonSerializer.Serialize(new { text = text.Trim() }, JsonOptions);
            using var response = await PostJsonWithRetryAsync("segment", requestJson, cancellationToken)
                .ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                VoiceScreenLog.Warn($"Semantic segmenter unavailable: status={(int)response.StatusCode}");
                return false;
            }
            return JsonSerializer.Deserialize<SegmentResponse>(body, JsonOptions)?.Break == true;
        }
        finally
        {
            _translationGate.Release();
        }
    }

    /// <summary>
    /// 按 <see cref="TranslationDirections.ToModelPair"/> 给出的模型对逐段翻译。
    /// 泰译中没有直接模型，会自动走 th-en -> en-zh 的英文桥接；桥接这件事在这里是
    /// 数据驱动的，不再是散落在各调用点的 if 分支。
    /// </summary>
    private async Task<string> TranslateThroughModelPairAsync(string text, TranslationDirection direction,
        int beamSize, CancellationToken cancellationToken)
    {
        var current = text;
        foreach (var modelPair in direction.ToModelPair())
            current = await TranslateTextWithGateAsync(current, modelPair, beamSize, cancellationToken)
                .ConfigureAwait(false);
        return current;
    }

    private static LocalIncomingTranslation EmptyIncoming(string language)
        => new(string.Empty, string.Empty, language);

    private async Task<LocalTranscription> TranscribeWithGateAsync(ReadOnlyMemory<byte> pcm16Mono16Khz,
        TranscriptionRequest request, CancellationToken cancellationToken)
    {
        await _speechGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await TranscribeAsync(pcm16Mono16Khz, request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _speechGate.Release();
        }
    }

    private async Task<string> TranslateTextWithGateAsync(string source, string modelPair, int beamSize,
        CancellationToken cancellationToken)
    {
        await _translationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await TranslateTextAsync(source, modelPair, beamSize, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _translationGate.Release();
        }
    }

    private async Task<LocalTranscription> TranscribeAsync(ReadOnlyMemory<byte> pcm16Mono16Khz,
        TranscriptionRequest request, CancellationToken cancellationToken)
    {
        using var response = await PostAudioWithRetryAsync(BuildTranscribePath(request), pcm16Mono16Khz,
            cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"本地语音识别失败：{ReadError(body)}");
        var result = JsonSerializer.Deserialize<TranscriptionResponse>(body, JsonOptions);
        return new LocalTranscription(result?.Text?.Trim() ?? string.Empty, result?.Language?.Trim() ?? string.Empty,
            ConvertWords(result?.Words));
    }

    private static string BuildTranscribePath(TranscriptionRequest request)
    {
        var path = $"transcribe?language={request.Language}&mode={(request.Preview ? "preview" : "final")}";
        if (request.WantWords) path += "&words=1";
        if (!string.IsNullOrEmpty(request.Session))
        {
            path += $"&session={Uri.EscapeDataString(request.Session)}";
            if (request.ResetSession) path += "&reset=1";
        }
        return path;
    }

    /// <summary>词级时间戳用的是紧凑字段名，转换成 Core 里可测试的语义类型。</summary>
    private static IReadOnlyList<TranscribedWord>? ConvertWords(IReadOnlyList<WordResponse>? words)
    {
        if (words is null || words.Count == 0) return null;
        var converted = new TranscribedWord[words.Count];
        for (var index = 0; index < words.Count; index++)
            converted[index] = new TranscribedWord(words[index].T ?? string.Empty, words[index].S, words[index].E);
        return converted;
    }

    private async Task<string> TranslateTextAsync(string source, string modelPair, int beamSize,
        CancellationToken cancellationToken)
    {
        var requestJson = JsonSerializer.Serialize(
            new { text = source, direction = modelPair, beamSize }, JsonOptions);
        using var translationResponse = await PostJsonWithRetryAsync("translate", requestJson, cancellationToken)
            .ConfigureAwait(false);
        var translationBody = await translationResponse.Content.ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!translationResponse.IsSuccessStatusCode)
            throw new InvalidOperationException($"模型服务翻译失败：{ReadError(translationBody)}");
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
        if (_remoteMode)
            throw new InvalidOperationException(
                $"Spark 模型服务不可用：{_asr.BaseAddress}。请确认 Spark 主机地址正确且 VoiceScreen 模型服务健康。");
        var script = Path.Combine(AppContext.BaseDirectory, "LocalService", "local_outgoing_service.py");
        if (!File.Exists(script)) throw new FileNotFoundException("缺少本地语音识别服务脚本。", script);
        var arguments = $"\"{script}\" --port {ServicePort} --asr-engine {_asrEngine} --asr-device {_asrDevice}";
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
        "qwen" or "qwen3" or "qwen3-asr" => "qwen3-asr",
        "sherpa" or "sherpa-onnx" or "zipformer" => "sherpa",
        _ => "whisper"
    };

    private static Uri ValidateServiceUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException("Spark 模型服务地址无效，请填写完整的 http:// 或 https:// 地址。");
        var normalized = uri.AbsoluteUri.EndsWith('/') ? uri : new Uri(uri.AbsoluteUri + "/");
        return normalized;
    }

    private async Task<bool> ReadQwenStreamingAvailabilityAsync(CancellationToken cancellationToken)
    {
        if (!_remoteMode) return false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            using var response = await _asr.GetAsync("health", timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return false;
            var json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("asrStreaming", out var value)
                   && value.ValueKind == JsonValueKind.True;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>auto 表示有 CUDA 就用；显卡忙或驱动不全时 Python 侧会自己退回 CPU。</summary>
    private static string NormalizeAsrDevice(string asrDevice) => asrDevice?.Trim().ToLowerInvariant() switch
    {
        "cuda" or "gpu" => "cuda",
        "cpu" => "cpu",
        _ => "auto"
    };

    /// <summary>ASR 和翻译两套模型都装载完成才算就绪，否则第一句话必然失败。</summary>
    private static async Task<bool> IsHealthyAsync(HttpClient client, string path,
        CancellationToken cancellationToken)
    {
        try
        {
            using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            requestTimeout.CancelAfter(TimeSpan.FromSeconds(2));
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

    private sealed record TranscriptionResponse(string Text, string Language, IReadOnlyList<WordResponse>? Words);

    /// <summary>词级时间戳的紧凑传输格式：t=文本, s=起始秒, e=结束秒。</summary>
    private sealed record WordResponse(string? T, double S, double E);

    private sealed record TranslationResponse(string Text);
    private sealed record SegmentResponse(bool Break);
}

/// <summary>一次识别请求的全部可选项。</summary>
/// <param name="Language">送给模型的语种提示；<c>auto</c> 表示让模型自己判断。</param>
/// <param name="Preview">true 走 base 临时模型，false 走 small 定稿模型。</param>
/// <param name="WantWords">是否要词级时间戳。只有需要裁剪滚动窗口的临时快照才要。</param>
/// <param name="Session">流式识别会话 id；Sherpa 与 Qwen realtime 使用，Whisper 会忽略。</param>
/// <param name="ResetSession">true 表示丢弃同名旧会话，用于一句话的第一个快照。</param>
public sealed record TranscriptionRequest
{
    public string Language { get; init; } = SpokenLanguage.Unknown;
    public bool Preview { get; init; }
    public bool WantWords { get; init; }
    public string? Session { get; init; }
    public bool ResetSession { get; init; }
}

public sealed record LocalOutgoingTranslation(string SourceText, string TranslatedText);
public sealed record LocalIncomingTranslation(string SourceText, string TranslatedText, string Language);
public sealed record LocalTranscription(string Text, string Language,
    IReadOnlyList<TranscribedWord>? Words = null);
