using VoiceScreen.Core;
using Xunit;

namespace VoiceScreen.Tests;

/// <summary>
/// 方向枚举原先在 C#、Python 的 translate_text、Python 的 evaluate_translation 里
/// 各有一份且已经不一致（th-en / th-zh 混用）。这里锁死 C# 侧的契约，
/// Python 侧由 tests/python/test_translation_eval_web.py 对应覆盖。
/// </summary>
public class TranslationDirectionTests
{
    [Theory]
    [InlineData(TranslationDirection.ChineseToEnglish, "zh-en")]
    [InlineData(TranslationDirection.EnglishToChinese, "en-zh")]
    [InlineData(TranslationDirection.ThaiToChinese, "th-zh")]
    public void 契约字符串与Python侧一致(TranslationDirection direction, string expected)
        => Assert.Equal(expected, direction.ToWireValue());

    [Fact]
    public void 泰译中必须经英文桥接()
    {
        // 没有 th-zh 的 OPUS-MT 模型，只能 th-en 再 en-zh。
        Assert.Equal(["th-en", "en-zh"], TranslationDirection.ThaiToChinese.ToModelPair());
    }

    [Theory]
    [InlineData(TranslationDirection.ChineseToEnglish, "zh-en")]
    [InlineData(TranslationDirection.EnglishToChinese, "en-zh")]
    public void 其余方向一步到位(TranslationDirection direction, string expected)
        => Assert.Equal([expected], direction.ToModelPair());

    [Theory]
    [InlineData("zh-en", TranslationDirection.ChineseToEnglish)]
    [InlineData("TH-ZH", TranslationDirection.ThaiToChinese)]
    [InlineData(" en-zh ", TranslationDirection.EnglishToChinese)]
    public void 解析忽略大小写和空白(string wire, TranslationDirection expected)
    {
        Assert.True(TranslationDirections.TryParse(wire, out var direction));
        Assert.Equal(expected, direction);
    }

    [Theory]
    [InlineData("th-en")]   // 模型对不是合法的用户方向
    [InlineData("ja-zh")]
    [InlineData(null)]
    public void 拒绝非用户方向(string? wire)
        => Assert.False(TranslationDirections.TryParse(wire, out _));
}
