using VoiceScreen.Core;
using Xunit;

namespace VoiceScreen.Tests;

/// <summary>
/// 真实幻觉样本回归。文本来自 tools/benchmark_asr.py 在 10dB SNR 下跑 whisper-base
/// 得到的实际输出：24 词的音频被识别成 223 词的 "or a lightning" 死循环。
/// 这类输出如果上屏会瞬间刷满整个字幕区。
/// </summary>
public class HallucinationRegressionTests
{
    private const string RealWhisperBaseHallucination =
        "and it has great notes when there's like a lightning or a lightning or a lightning "
        + "or a lightning or a lightning or a lightning or lightning or lightning or lightning "
        + "or lightning or lightning or lightning or lightning or lightning or lightning "
        + "or lightning or lightning or lightning or lightning or lightning or lightning";

    [Fact]
    public void 真实的Whisper幻觉循环会被拦下()
        => Assert.True(TranscriptSanitizer.IsPathologicalRepetition(RealWhisperBaseHallucination));

    [Fact]
    public void 同一段噪声下的正常识别结果不会被误杀()
    {
        // 同一批次、同样 10dB SNR 条件下 whisper-small 的真实输出，WER 约 18%，
        // 内容有误但完全可用，绝不能被当成幻觉丢掉。
        const string noisyButUsable =
            "Mr. Quilter is the apostle of the middle classes. We are glad to welcome his gospel.";
        Assert.False(TranscriptSanitizer.IsPathologicalRepetition(noisyButUsable));
    }
}
