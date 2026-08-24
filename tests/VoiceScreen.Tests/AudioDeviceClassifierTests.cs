using VoiceScreen.Core;
using Xunit;

namespace VoiceScreen.Tests;

public sealed class AudioDeviceClassifierTests
{
    [Theory]
    [InlineData("CABLE Input (VB-Audio Virtual Cable)")]
    [InlineData("cable input (vb-audio virtual cable)")]
    public void CableInputIsTheOnlyAllowedSendEndpoint(string name)
    {
        Assert.True(AudioDeviceClassifier.IsVirtualCableSendEndpoint(name));
    }

    [Theory]
    [InlineData("CABLE In 16ch (VB-Audio Virtual Cable)")]
    [InlineData("CABLE Output (VB-Audio Virtual Cable)")]
    [InlineData("VB-Audio VoiceMeeter VAIO")]
    public void EveryVirtualAudioEndpointIsRejectedAsPhysicalMonitor(string name)
    {
        Assert.True(AudioDeviceClassifier.IsVirtualAudioDevice(name));
        Assert.False(AudioDeviceClassifier.IsVirtualCableSendEndpoint(name));
    }

    [Theory]
    [InlineData("扬声器 (HyperX Virtual Surround Sound)")]
    [InlineData("Headphones (Realtek(R) Audio)")]
    [InlineData(null)]
    [InlineData("")]
    public void PhysicalAndEmptyNamesAreNotVirtual(string? name)
    {
        Assert.False(AudioDeviceClassifier.IsVirtualAudioDevice(name));
        Assert.False(AudioDeviceClassifier.IsVirtualCableSendEndpoint(name));
    }
}
