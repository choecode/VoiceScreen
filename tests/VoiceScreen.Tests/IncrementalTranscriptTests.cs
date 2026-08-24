using VoiceScreen.Core;
using Xunit;

namespace VoiceScreen.Tests;

/// <summary>低延迟字幕闪烁与否全看这里，之前完全没有测试覆盖。</summary>
public class IncrementalTranscriptTests
{
    [Fact]
    public void 中文取字符级公共前缀()
    {
        var stable = IncrementalTranscript.LongestStablePrefix("敌人在二楼", "敌人在二楼右边", "zh");
        Assert.Equal("敌人在二楼", stable);
    }

    [Fact]
    public void 英文回退到词边界避免半个单词进翻译()
    {
        // 两次假设都以 "Enemies are on the sec" 开头，但 "sec" 是半个词，
        // 必须退到上一个空格，否则 OPUS-MT 会对着残词乱猜。
        var stable = IncrementalTranscript.LongestStablePrefix(
            "Enemies are on the second", "Enemies are on the sector", "en");
        Assert.Equal("Enemies are on the", stable);
    }

    [Fact]
    public void 英文没有词边界时整体放弃()
    {
        // 公共前缀只有 "Hel"，找不到词边界。返回半个词会污染译文，所以宁可等下一次快照。
        var stable = IncrementalTranscript.LongestStablePrefix("Hello", "Help", "en");
        Assert.Equal(string.Empty, stable);
    }

    [Fact]
    public void 完全不同的假设没有稳定前缀()
        => Assert.Equal(string.Empty, IncrementalTranscript.LongestStablePrefix("abc", "xyz", "en"));

    [Theory]
    [InlineData(null, "abc")]
    [InlineData("abc", null)]
    [InlineData("", "abc")]
    public void 空输入返回空前缀(string? previous, string? current)
        => Assert.Equal(string.Empty, IncrementalTranscript.LongestStablePrefix(previous, current, "en"));

    [Fact]
    public void 泰文的稳定长度门槛更高()
    {
        // 泰文没有词间空格，单字符信息量低，门槛抬到 4。
        Assert.Equal(4, IncrementalTranscript.MinimumStableLength("th"));
        Assert.Equal(3, IncrementalTranscript.MinimumStableLength("en"));
        Assert.Equal(3, IncrementalTranscript.MinimumStableLength("zh"));
    }

    [Theory]
    [InlineData("Let's go.", true)]
    [InlineData("走吧。", true)]
    [InlineData("真的吗？", true)]
    [InlineData("快跑！", true)]
    [InlineData("Enemies are", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void 句末标点判定(string? text, bool expected)
        => Assert.Equal(expected, IncrementalTranscript.EndsClause(text));

    [Fact]
    public void 累计快照只返回尚未翻译的新尾部()
    {
        var suffix = IncrementalTranscript.NewlyStableSuffix(
            "Yeah, it's basically that, but on crack. They found a way.",
            "Yeah, it's basically that, but on crack. They found a way, and there's no hack like that.");

        Assert.Equal("and there's no hack like that.", suffix);
    }

    [Fact]
    public void 标点与空白修订不会让整段再次翻译()
    {
        var suffix = IncrementalTranscript.NewlyStableSuffix(
            "Oh, what the fuck! That's crazy. They made a thing.",
            "Oh what the fuck — that's crazy; they made a thing. They found a way.");

        Assert.Equal("They found a way.", suffix);
    }

    [Fact]
    public void 早先一个词被修正后仍按词对齐新尾部()
    {
        var suffix = IncrementalTranscript.NewlyStableSuffix(
            "They basically made a thing and found a way.",
            "They actually made a thing and found a way. It worked.");

        Assert.Equal("It worked.", suffix);
    }

    [Fact]
    public void 无法对齐的识别回卷不会重复追加整段()
        => Assert.Equal(string.Empty,
            IncrementalTranscript.NewlyStableSuffix("They found a way.", "A completely different hypothesis."));

    [Fact]
    public void 没有旧前缀时完整返回当前稳定文本()
        => Assert.Equal("First stable clause.",
            IncrementalTranscript.NewlyStableSuffix(null, "First stable clause."));
}
