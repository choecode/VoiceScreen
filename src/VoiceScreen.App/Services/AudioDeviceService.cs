using NAudio.CoreAudioApi;
using VoiceScreen.App.Models;

namespace VoiceScreen.App.Services;

public sealed class AudioDeviceService
{
    public static bool IsVirtualCableInput(AudioDeviceOption device)
        => device.Name.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase)
           && device.Name.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<AudioDeviceOption> GetCaptureDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        return enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
            .Select(d => new AudioDeviceOption(d.ID, d.FriendlyName))
            .OrderBy(d => d.Name)
            .ToArray();
    }

    public IReadOnlyList<AudioDeviceOption> GetRenderDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        return enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .Select(d => new AudioDeviceOption(d.ID, d.FriendlyName))
            .OrderBy(d => d.Name)
            .ToArray();
    }

    public static AudioDeviceOption? FindBest(IEnumerable<AudioDeviceOption> devices, string savedId, params string[] preferredTerms)
    {
        var list = devices.ToList();
        var saved = list.FirstOrDefault(d => d.Id == savedId);
        if (saved is not null) return saved;
        foreach (var term in preferredTerms)
        {
            var match = list.FirstOrDefault(d => d.Name.Contains(term, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;
        }
        return list.FirstOrDefault();
    }

    /// <summary>虚拟麦克风的播放端必须严格匹配 CABLE Input，不能回退到任意扬声器。</summary>
    public static AudioDeviceOption? FindVirtualCableInput(IEnumerable<AudioDeviceOption> devices)
        => devices.FirstOrDefault(IsVirtualCableInput);
}
