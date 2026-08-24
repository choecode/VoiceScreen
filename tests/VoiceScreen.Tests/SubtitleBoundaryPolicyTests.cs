using VoiceScreen.Core;
using Xunit;

namespace VoiceScreen.Tests;

public class SubtitleBoundaryPolicyTests
{
    [Fact]
    public void 语音不足三秒时不请求语义模型()
        => Assert.False(SubtitleBoundaryPolicy.ShouldRequestSemanticDecision(
            "This is already a complete sentence.", 50, 0));

    [Fact]
    public void 完整候选句积累足够上下文后请求语义判断()
        => Assert.True(SubtitleBoundaryPolicy.ShouldRequestSemanticDecision(
            "This is already a complete sentence.", 75, 0));

    [Fact]
    public void 语义请求有两秒冷却避免压垮翻译模型()
        => Assert.False(SubtitleBoundaryPolicy.ShouldRequestSemanticDecision(
            "This is already a complete sentence.", 100, 75));

    [Fact]
    public void 只有语义通过并出现短暂停顿才收尾()
    {
        Assert.False(SubtitleBoundaryPolicy.ShouldCompleteAtSemanticPause(true, 2, 100, 90));
        Assert.True(SubtitleBoundaryPolicy.ShouldCompleteAtSemanticPause(true, 3, 100, 90));
        Assert.False(SubtitleBoundaryPolicy.ShouldCompleteAtSemanticPause(false, 3, 100, 90));
    }

    [Fact]
    public void 过期的语义判断不会在后续停顿误切下一句话()
        => Assert.False(SubtitleBoundaryPolicy.ShouldCompleteAtSemanticPause(true, 3, 160, 90));
}
