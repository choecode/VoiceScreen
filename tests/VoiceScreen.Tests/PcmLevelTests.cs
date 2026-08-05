using VoiceScreen.Core;
using Xunit;

namespace VoiceScreen.Tests;

/// <summary>
/// RMS 门限决定一句话从哪里开始、到哪里结束，是整条实时字幕链路的第一道判断。
/// </summary>
public class PcmLevelTests
{
    private const int VoiceRmsThreshold = 120; // 与 LocalIncomingAudioProcessor 保持一致

    [Fact]
    public void 静音帧电平为零()
    {
        var silence = new byte[1280];
        Assert.Equal(0, PcmLevel.CalculateRms(silence));
    }

    [Fact]
    public void 空缓冲不会除零()
        => Assert.Equal(0, PcmLevel.CalculateRms([]));

    [Fact]
    public void 恒定幅度的RMS等于该幅度()
    {
        var pcm = MakeConstant(1000, samples: 640);
        Assert.Equal(1000, PcmLevel.CalculateRms(pcm), precision: 6);
    }

    [Fact]
    public void 负幅度同样计入能量()
    {
        // 平方和不区分符号，负半周期必须和正半周期给出相同电平。
        Assert.Equal(PcmLevel.CalculateRms(MakeConstant(1000, 640)),
            PcmLevel.CalculateRms(MakeConstant(-1000, 640)), precision: 6);
    }

    [Fact]
    public void 说话电平高于门限而底噪低于门限()
    {
        Assert.True(PcmLevel.CalculateRms(MakeConstant(3000, 640)) >= VoiceRmsThreshold);
        Assert.True(PcmLevel.CalculateRms(MakeConstant(20, 640)) < VoiceRmsThreshold);
    }

    [Fact]
    public void 只读取传入的那一段()
    {
        // 调用方会传 ArrayPool 租来的缓冲的前半段，多余的尾部不能参与计算。
        var buffer = new byte[2560];
        MakeConstant(3000, 640).CopyTo(buffer, 0);
        Assert.Equal(0, PcmLevel.CalculateRms(buffer.AsSpan(1280, 1280)));
    }

    private static byte[] MakeConstant(short amplitude, int samples)
    {
        var pcm = new byte[samples * 2];
        for (var index = 0; index < samples; index++)
        {
            pcm[index * 2] = (byte)(amplitude & 0xFF);
            pcm[index * 2 + 1] = (byte)((amplitude >> 8) & 0xFF);
        }
        return pcm;
    }
}
