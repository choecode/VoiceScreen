namespace VoiceScreen.Core;

/// <summary>
/// 语种判定的唯一实现。
///
/// 中文 / 泰文的 Unicode 区间判断此前被复制了四份（Python 服务、LocalOutgoingService、
/// LocalIncomingAudioProcessor、OnlineApiService），加一门语言要改四处，而且四处的
/// 回退行为已经出现分歧。统一到这里之后，ASR 的语种标签只作为回退，文本本身的字符
/// 分布优先——Whisper 在短句上给出的 language 字段并不可靠。
/// </summary>
public static class SpokenLanguage
{
    public const string Chinese = "zh";
    public const string English = "en";
    public const string Thai = "th";
    public const string Unknown = "auto";

    // CJK 统一表意文字扩展 A（U+3400）到基本区末尾（U+9FFF）。
    public static bool ContainsChinese(string? text)
        => text is not null && text.Any(character => character is >= '㐀' and <= '鿿');

    // 泰文块 U+0E00–U+0E7F。
    public static bool ContainsThai(string? text)
        => text is not null && text.Any(character => character is >= '฀' and <= '๿');

    /// <summary>
    /// 优先用文本字符判定，判不出来时才退回 ASR 报告的语种标签。
    /// </summary>
    public static string Detect(string? text, string? reportedLanguage)
    {
        if (ContainsChinese(text)) return Chinese;
        if (ContainsThai(text)) return Thai;

        var reported = reportedLanguage?.Trim();
        if (!string.IsNullOrEmpty(reported) && !reported.Equals(Unknown, StringComparison.OrdinalIgnoreCase))
        {
            if (Is(reported, Chinese)) return Chinese;
            if (Is(reported, Thai)) return Thai;
            if (Is(reported, English)) return English;
            return reported;
        }

        return text is not null && text.Any(char.IsLetter) ? English : Unknown;
    }

    /// <summary>语种标签前缀匹配，兼容 "zh"、"zh-CN"、"en-US" 这类写法。</summary>
    public static bool Is(string? language, string expected)
        => language is not null && language.StartsWith(expected, StringComparison.OrdinalIgnoreCase);

    /// <summary>接收侧只处理中/英/泰三种，其余直接丢弃避免脏字幕。</summary>
    public static bool IsSupportedIncoming(string? language)
        => Is(language, Chinese) || Is(language, English) || Is(language, Thai);
}
