namespace VoiceScreen.App.Models;

public sealed class AppSettings
{
    public bool DemoMode { get; set; }
    // CloudMode is retained only to migrate settings written by older releases.
    public bool CloudMode { get; set; }
    public string AsrEngine { get; set; } = "qwen3-asr";
    public string ModelServiceUrl { get; set; } = "http://spark-host.local:18765/";
    public string ModelServiceToken { get; set; } = string.Empty;

    /// <summary>
    /// Whisper 推理设备：auto（有 CUDA 就用）、cuda 或 cpu。
    /// GPU 上 small 只占约 0.5GB 显存，换来 5–10 倍的识别速度；显卡被游戏占满或
    /// 驱动不全时本地服务会自己退回 CPU，不需要用户干预。
    /// </summary>
    public string AsrDevice { get; set; } = "auto";

    public bool UseApiTranslation { get; set; }
    public bool UseApiTts { get; set; }
    public bool LowLatencyIncoming { get; set; } = true;
    /// <summary>
    /// 是否启用麦克风中文识别、翻译和通过 VB-CABLE 发送英文语音。
    /// 接收所选进程字幕与这条链路独立；发送设备异常时引擎也会自动降级为仅字幕模式。
    /// </summary>
    public bool EnableOutgoingTranslation { get; set; } = true;

    /// <summary>
    /// 按住右 Alt 期间，说完一个短句就抢先翻译并送进 Discord，不等松手。
    /// 对方能提早一到三秒听到第一句，代价是已经播出的英文收不回来，
    /// 所以默认关闭，由用户显式打开。
    /// </summary>
    public bool OutgoingClauseStreaming { get; set; }
    public string MicrophoneDeviceId { get; set; } = string.Empty;
    public string DiscordOutputDeviceId { get; set; } = string.Empty;
    /// <summary>
    /// 接收字幕所监听的进程。进程 ID 每次启动都会变化，因此只持久化可执行文件名和路径，
    /// 运行时再解析当前根进程并捕获它的完整子进程树。
    /// </summary>
    public string IncomingProcessName { get; set; } = string.Empty;
    public string IncomingProcessPath { get; set; } = string.Empty;
    /// <summary>
    /// 用户本次选中的精确根进程。它只用于当前实例仍存活时避免同路径多实例选错；
    /// 应用重启导致 PID 变化后会自动回退到名称和路径重新解析。
    /// </summary>
    public int IncomingProcessId { get; set; }
    public string CableRenderDeviceId { get; set; } = string.Empty;
    public string MonitorRenderDeviceId { get; set; } = string.Empty;
    public bool MonitorTranslatedSpeech { get; set; } = true;
    public string EnglishVoiceName { get; set; } = string.Empty;
    /// <summary>
    /// 这台机器已经完成的私有音色配置版本。首次收到 my-voice 声纹配置后自动选中一次，
    /// 后续用户再切换 Windows 音色时不应被每次启动强行改回。
    /// </summary>
    public int PrivateVoiceProfileVersion { get; set; }
    public string ApiEnglishVoice { get; set; } = "en-US-JennyNeural";
    public int MaxSubtitleLines { get; set; } = 8;
    public double OverlayLeft { get; set; } = 20;
    public double OverlayTop { get; set; } = 20;
    public double OverlayWidth { get; set; } = 680;
    public double OverlayHeight { get; set; } = 300;
    /// <summary>保留字段兼容旧设置文件；当前产品固定使用 14 号字幕。</summary>
    public double SubtitleFontSize { get; set; } = 14;
    /// <summary>
    /// 保留此字段用于兼容旧设置文件；当前版本接收方向始终只抓所选进程树。
    /// </summary>
    public bool UseProcessLoopback { get; set; } = true;
}

public sealed record AudioDeviceOption(string Id, string Name)
{
    public override string ToString() => Name;
}

public sealed record SpeechVoiceOption(string Id, string Name)
{
    public override string ToString() => Name;
}

public sealed record ApiVoiceOption(string Id, string Name)
{
    public override string ToString() => Name;
}

public sealed record ProcessAudioTarget(
    int ProcessId,
    string ProcessName,
    string ExecutablePath,
    string WindowTitle,
    string ProductName)
{
    public string DisplayName
    {
        get
        {
            var app = string.IsNullOrWhiteSpace(ProductName) ? ProcessName : ProductName;
            var title = string.IsNullOrWhiteSpace(WindowTitle) ? string.Empty : $" · {WindowTitle}";
            return $"{app} · PID {ProcessId}{title}";
        }
    }

    public override string ToString() => DisplayName;
}
