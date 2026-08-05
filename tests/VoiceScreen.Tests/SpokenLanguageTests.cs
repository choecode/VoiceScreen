using VoiceScreen.Core;
using Xunit;

namespace VoiceScreen.Tests;

/// <summary>
/// 语种判定此前在四个文件里各有一份拷贝且已经出现分歧。这组测试固定住统一后的语义。
/// </summary>
public class SpokenLanguageTests
{
    [Theory]
    [InlineData("敌人在二楼", SpokenLanguage.Chinese)]
    [InlineData("สวัสดีครับ", SpokenLanguage.Thai)]
    [InlineData("Enemies upstairs", SpokenLanguage.English)]
    public void 文本字符决定语种(string text, string expected)
        => Assert.Equal(expected, SpokenLanguage.Detect(text, "auto"));

    [Fact]
    public void 文本字符优先于ASR报告的语种()
    {
        // Whisper 在短中文句上经常误报成 en，这里必须以字符为准。
        Assert.Equal(SpokenLanguage.Chinese, SpokenLanguage.Detect("好的", "en"));
    }

    [Fact]
    public void 判不出来时才退回ASR的标签()
    {
        // 纯数字没有字符线索，只能信 ASR。
        Assert.Equal("ja", SpokenLanguage.Detect("123", "ja"));
    }

    [Theory]
    [InlineData("", SpokenLanguage.Unknown)]
    [InlineData("123", SpokenLanguage.Unknown)]
    [InlineData("!!!", SpokenLanguage.Unknown)]
    public void 没有任何线索时返回未知(string text, string expected)
        => Assert.Equal(expected, SpokenLanguage.Detect(text, "auto"));

    [Theory]
    [InlineData("zh-CN", SpokenLanguage.Chinese)]
    [InlineData("en-US", SpokenLanguage.English)]
    public void 语种标签按前缀匹配(string reported, string expected)
        => Assert.Equal(expected, SpokenLanguage.Detect("123", reported));

    [Theory]
    [InlineData("zh", true)]
    [InlineData("zh-CN", true)]
    [InlineData("en-US", true)]
    [InlineData("th", true)]
    [InlineData("ja", false)]
    [InlineData(null, false)]
    public void 接收侧只接受中英泰(string? language, bool expected)
        => Assert.Equal(expected, SpokenLanguage.IsSupportedIncoming(language));
}
