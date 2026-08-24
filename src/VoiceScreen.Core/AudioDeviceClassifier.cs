namespace VoiceScreen.Core;

/// <summary>以稳定的设备名称规则区分实体音频端点与虚拟音频线。</summary>
public static class AudioDeviceClassifier
{
    /// <summary>VoiceScreen 发送翻译语音时唯一允许使用的 VB-CABLE 播放端点。</summary>
    public static bool IsVirtualCableSendEndpoint(string? name)
        => !string.IsNullOrWhiteSpace(name)
           && name.StartsWith("CABLE Input", StringComparison.OrdinalIgnoreCase)
           && name.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 所有 VB-Audio 端点都不能作为实体麦克风或本地监听耳机；其中包括 Pack45
    /// 新增的 CABLE In 16ch，不能只排除旧名称 CABLE Input/Output。
    /// </summary>
    public static bool IsVirtualAudioDevice(string? name)
        => !string.IsNullOrWhiteSpace(name)
           && (name.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase)
               || name.StartsWith("CABLE ", StringComparison.OrdinalIgnoreCase));
}
