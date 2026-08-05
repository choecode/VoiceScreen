using VoiceScreen.Core;
using Xunit;

namespace VoiceScreen.Tests;

/// <summary>
/// 这些阈值原先埋在 WPF 项目里，只能靠肉眼调。固定住"该拦的拦、该放的放"两侧，
/// 以后改阈值时会立刻知道是不是伤到了正常字幕。
/// </summary>
public class TranscriptSanitizerTests
{
    [Theory]
    [InlineData("啊啊啊啊啊啊啊啊啊啊")]                          // 单字符卡死
    [InlineData("aaaaaaaaaaaaaaaa")]
    [InlineData("我去哪了我去哪了我去哪了我去哪了我去哪了")]        // 中文短语周期性复读
    [InlineData("go go go go go go go go")]                      // 英文按词重复
    public void 病态重复会被拦下(string text)
        => Assert.True(TranscriptSanitizer.IsPathologicalRepetition(text));

    [Theory]
    [InlineData("敌人在二楼，我们从左边走。")]
    [InlineData("Enemies are on the second floor, let's move left.")]
    [InlineData("好的")]
    [InlineData("go go go")]                                      // 正常的口语强调，不该误杀
    [InlineData("")]
    [InlineData("   ")]
    public void 正常语句不会被误杀(string text)
        => Assert.False(TranscriptSanitizer.IsPathologicalRepetition(text));

    [Fact]
    public void 译文异常膨胀会被判定为不安全()
    {
        var source = "好的";
        var translated = new string('x', 200);
        Assert.True(TranscriptSanitizer.IsUnsafeTranslation(source, translated));
    }

    [Fact]
    public void 长原文允许成比例的长译文()
    {
        var source = new string('中', 100);
        var translated = "This is a perfectly reasonable translation of a long sentence.";
        Assert.False(TranscriptSanitizer.IsUnsafeTranslation(source, translated));
    }

    [Fact]
    public void 空译文不算不安全()
        => Assert.False(TranscriptSanitizer.IsUnsafeTranslation("anything", ""));

    [Fact]
    public void 短原文也有最低宽容长度()
    {
        // 长度上限是 max(120, 原文*12)：否则"嗯"翻成一句完整的话就会因为倍数超限被误杀。
        var translated = "Yeah, I think we should probably wait here for a moment before we move.";
        Assert.True(translated.Length is > 12 and <= 120);
        Assert.False(TranscriptSanitizer.IsUnsafeTranslation("嗯", translated));
    }
}
