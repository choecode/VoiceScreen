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
}
