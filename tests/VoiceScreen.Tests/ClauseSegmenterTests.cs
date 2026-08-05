using VoiceScreen.Core;
using Xunit;

namespace VoiceScreen.Tests;

/// <summary>
/// 分句抢跑的判定。这里判错的代价比字幕高一个量级：字幕可以改，
/// 已经通过 VB-CABLE 播给对方的英文收不回来。
/// </summary>
public class ClauseSegmenterTests
{
    [Fact]
    public void 逗号处切出第一个完整短句()
    {
        var split = ClauseSegmenter.Split("敌人在二楼，我们从左边走", 0);
        Assert.Equal(["敌人在二楼，"], split.Clauses);
        Assert.Equal(6, split.ConsumedCharacters);
    }

    [Fact]
    public void 一次可以切出多个短句()
    {
        var split = ClauseSegmenter.Split("敌人在二楼，我们从左边走，你先别冲。", 0);
        Assert.Equal(["敌人在二楼，", "我们从左边走，", "你先别冲。"], split.Clauses);
        Assert.Equal(18, split.ConsumedCharacters);
    }

    [Fact]
    public void 已经发过的部分不会重复发()
    {
        var first = ClauseSegmenter.Split("敌人在二楼，我们从左边走，", 0);
        var second = ClauseSegmenter.Split("敌人在二楼，我们从左边走，你先别冲。", first.ConsumedCharacters);
        Assert.Equal(["你先别冲。"], second.Clauses);
    }

    [Fact]
    public void 没有标点时不发出任何东西()
    {
        // 没说完的半句绝不能抢跑：对方会听到一段没有结尾的英文。
        var split = ClauseSegmenter.Split("敌人在二楼我们", 0);
        Assert.Empty(split.Clauses);
        Assert.Equal(0, split.ConsumedCharacters);
    }

    [Fact]
    public void 太短的语气片段并入下一句()
    {
        // 「嗯，」单独合成一段只会在对方耳朵里插进一个突兀的停顿。
        var split = ClauseSegmenter.Split("嗯，敌人在二楼。", 0);
        Assert.Equal(["嗯，敌人在二楼。"], split.Clauses);
    }

    [Fact]
    public void 连续标点不会被当成一句()
    {
        var split = ClauseSegmenter.Split("，，，，，", 0);
        Assert.Empty(split.Clauses);
    }

    [Fact]
    public void 英文标点同样有效()
    {
        var split = ClauseSegmenter.Split("Move left, then push.", 0);
        Assert.Equal(["Move left,", "then push."], split.Clauses);
    }

    [Fact]
    public void 识别结果回退时不会越界()
    {
        // 已消费长度大于当前文本，说明识别结果整体缩短了；要能自愈而不是抛异常。
        var split = ClauseSegmenter.Split("短", 99);
        Assert.Empty(split.Clauses);
        Assert.Equal(1, split.ConsumedCharacters);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void 空输入安全(string? text)
        => Assert.Empty(ClauseSegmenter.Split(text, 0).Clauses);

    [Fact]
    public void 收尾时取出未发送的尾巴()
    {
        var remainder = ClauseSegmenter.Remainder("敌人在二楼，我们从左边走", 6);
        Assert.Equal("我们从左边走", remainder);
    }

    [Fact]
    public void 整句都已发送时尾巴为空()
        => Assert.Equal(string.Empty, ClauseSegmenter.Remainder("敌人在二楼，", 6));

    [Fact]
    public void 没有抢跑过时尾巴就是整句()
        => Assert.Equal("敌人在二楼", ClauseSegmenter.Remainder("敌人在二楼", 0));

    [Fact]
    public void 按内容对齐切掉已播出的部分()
    {
        var remainder = ClauseSegmenter.RemainderAfterSpoken("敌人在二楼，我们从左边走。", "敌人在二楼，");
        Assert.Equal("我们从左边走。", remainder);
    }

    [Fact]
    public void 定稿模型多认出几个字时仍能对齐()
    {
        // 抢跑用 base 临时模型，收尾用 small 定稿模型，两者字数本来就会不同。
        // 按字数硬切会切错位置；按内容对齐才切得准。
        var remainder = ClauseSegmenter.RemainderAfterSpoken("敌人可能在二楼，我们从左边走。", "敌人可能在二楼，");
        Assert.Equal("我们从左边走。", remainder);
    }

    [Fact]
    public void 两次识别分歧太大时退回按字数切()
    {
        // 对不上就说明没法安全对齐。此时宁可少说，也不能让对方把同一句听两遍。
        var remainder = ClauseSegmenter.RemainderAfterSpoken("完全不同的一句话内容", "敌人在二楼，");
        Assert.Equal("句话内容", remainder);
    }

    [Fact]
    public void 没有抢跑过时整句都要发()
    {
        Assert.Equal("敌人在二楼。", ClauseSegmenter.RemainderAfterSpoken("敌人在二楼。", ""));
        Assert.Equal("敌人在二楼。", ClauseSegmenter.RemainderAfterSpoken("敌人在二楼。", null));
    }

    [Fact]
    public void 整句都已抢跑发完时不再重复()
        => Assert.Equal(string.Empty, ClauseSegmenter.RemainderAfterSpoken("敌人在二楼。", "敌人在二楼。"));

    [Fact]
    public void 对齐忽略空白差异()
    {
        // 英文识别在同一处可能给出不同的空格切分，空白不该影响对齐位置。
        var remainder = ClauseSegmenter.RemainderAfterSpoken("Move left, then push.", "Move left,");
        Assert.Equal("then push.", remainder);
    }
}
