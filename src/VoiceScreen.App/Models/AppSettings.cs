namespace VoiceScreen.App.Models;

public sealed class AppSettings
{
    public bool DemoMode { get; set; }
    // CloudMode is retained only to migrate settings written by older releases.
    public bool CloudMode { get; set; }
    public string AsrEngine { get; set; } = "whisper";
    public bool UseApiTranslation { get; set; }
    public bool UseApiTts { get; set; }
    public bool LowLatencyIncoming { get; set; } = true;
    public string MicrophoneDeviceId { get; set; } = string.Empty;
    public string DiscordOutputDeviceId { get; set; } = string.Empty;
    public string CableRenderDeviceId { get; set; } = string.Empty;
    public string MonitorRenderDeviceId { get; set; } = string.Empty;
    public bool MonitorTranslatedSpeech { get; set; } = true;
    public string EnglishVoiceName { get; set; } = string.Empty;
    public string ApiEnglishVoice { get; set; } = "en-US-JennyNeural";
    public int MaxSubtitleLines { get; set; } = 8;
    public double OverlayLeft { get; set; } = 20;
    public double OverlayTop { get; set; } = 20;
    public double OverlayWidth { get; set; } = 680;
    public double OverlayHeight { get; set; } = 300;
    public double SubtitleFontSize { get; set; } = 24;
    /// <summary>
    /// 保留此字段仅用于兼容旧设置文件；当前版本接收方向始终只抓 Discord 进程树。
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
