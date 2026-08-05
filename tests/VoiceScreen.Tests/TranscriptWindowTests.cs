using VoiceScreen.Core;
using Xunit;

namespace VoiceScreen.Tests;

/// <summary>
/// 滚动窗口裁剪是低延迟模式不随句子变长而变慢的关键。裁剪点算错的后果分两种：
/// 少裁只是慢一点，多裁会把还没识别的音频丢掉，用户看到的是句子中间凭空少了几个词。
/// </summary>
public class TranscriptWindowTests
{
    private static TranscribedWord[] EnglishWords() =>
    [
        new(" Enemies", 0.00, 0.62),
        new(" are", 0.62, 0.80),
        new(" on", 0.80, 0.95),
        new(" the", 0.95, 1.10),
        new(" second", 1.10, 1.58),
        new(" floor", 1.58, 2.05),
    ];

    [Fact]
    public void 裁剪点落在已确认的最后一个词上()
    {
        var seconds = TranscriptWindow.CommittedEndSeconds(EnglishWords(), "Enemies are on the");
        // 1.10 是 "the" 的结束时间，再减去安全余量。
        Assert.Equal(1.10 - TranscriptWindow.TrimSafetyMarginSeconds, seconds, 3);
    }

    [Fact]
    public void 已确认整句时裁掉整个窗口()
    {
        var seconds = TranscriptWindow.CommittedEndSeconds(EnglishWords(), "Enemies are on the second floor");
        Assert.Equal(2.05 - TranscriptWindow.TrimSafetyMarginSeconds, seconds, 3);
    }

    [Fact]
    public void 已确认文本不足一个完整词时不裁剪()
    {
        // "Enem" 是半个词。裁在这里会把 "ies" 的音频切掉，下一次识别再也补不回来。
        Assert.Equal(0, TranscriptWindow.CommittedEndSeconds(EnglishWords(), "Enem"));
    }

    [Fact]
    public void 词表和已确认文本对不上时不裁剪()
        => Assert.Equal(0, TranscriptWindow.CommittedEndSeconds(EnglishWords(), "Something else entirely"));

    [Fact]
    public void 中文按字符匹配不需要空格()
    {
        TranscribedWord[] words =
        [
            new("敌人", 0.0, 0.5),
            new("在", 0.5, 0.7),
            new("二楼", 0.7, 1.2),
        ];
        Assert.Equal(0.7 - TranscriptWindow.TrimSafetyMarginSeconds,
            TranscriptWindow.CommittedEndSeconds(words, "敌人在"), 3);
    }

    [Fact]
    public void 没有词级时间戳时不裁剪()
    {
        Assert.Equal(0, TranscriptWindow.CommittedEndSeconds(null, "anything"));
        Assert.Equal(0, TranscriptWindow.CommittedEndSeconds([], "anything"));
    }

    [Fact]
    public void 空的已确认文本不裁剪()
        => Assert.Equal(0, TranscriptWindow.CommittedEndSeconds(EnglishWords(), ""));

    [Fact]
    public void 安全余量不会让裁剪点变成负数()
    {
        TranscribedWord[] words = [new("Hi", 0.0, 0.05)];
        Assert.Equal(0, TranscriptWindow.CommittedEndSeconds(words, "Hi"));
    }

    [Fact]
    public void 上一次假设按已确认前缀重新对齐()
    {
        // 窗口裁剪之后，上一次的假设也要去掉同一段前缀，否则 LocalAgreement 会拿
        // 旧窗口的文本和新窗口的文本比较，公共前缀必然为空，稳定判定就此瘫痪。
        var rebased = TranscriptWindow.RebasePreviousHypothesis(
            "Enemies are on the second floor", "Enemies are on the");
        Assert.Equal("second floor", rebased);
    }

    [Fact]
    public void 上一次假设不以已确认前缀开头时整体丢弃()
        => Assert.Equal(string.Empty, TranscriptWindow.RebasePreviousHypothesis("totally different", "Enemies"));

    [Fact]
    public void 英文拼接补空格中文不补()
    {
        Assert.Equal("Enemies are on the second floor",
            TranscriptWindow.Join("Enemies are on the", "second floor"));
        Assert.Equal("敌人在二楼右边", TranscriptWindow.Join("敌人在二楼", "右边"));
    }

    [Fact]
    public void 中英混排在中文一侧不补空格()
    {
        // 已确认部分以中文标点结尾时补空格会在字幕里留下一个突兀的缝。
        Assert.Equal("敌人在二楼，right side", TranscriptWindow.Join("敌人在二楼，", "right side"));
    }

    [Theory]
    [InlineData(null, "tail", "tail")]
    [InlineData("head", null, "head")]
    [InlineData("", "", "")]
    public void 拼接容忍空值(string? committed, string? window, string expected)
        => Assert.Equal(expected, TranscriptWindow.Join(committed, window));
}
