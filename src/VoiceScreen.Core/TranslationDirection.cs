namespace VoiceScreen.Core;

/// <summary>
/// 翻译方向的唯一定义。
///
/// 这里要区分两个此前被混用的概念：
/// <list type="bullet">
/// <item><b>用户方向</b>（<see cref="ThaiToChinese"/> 等）——界面和 HTTP 契约上暴露的方向。</item>
/// <item><b>模型对</b>（<see cref="ToModelPair"/>）——实际存在的 OPUS-MT 模型。
/// 泰译中没有直接模型，要经英文桥接，所以 th-zh 会落到 th-en + en-zh 两步。</item>
/// </list>
/// 之前 C# 侧、Python 的 <c>translate_text</c>、Python 的 <c>evaluate_translation</c>
/// 各自维护了一份方向列表且已经不一致（th-en / th-zh 混用），泰语的术语表标记因此恒为假。
/// </summary>
public enum TranslationDirection
{
    ChineseToEnglish,
    EnglishToChinese,
    ThaiToChinese
}

public static class TranslationDirections
{
    /// <summary>HTTP 契约上的方向字符串，和 Python 侧 <c>USER_DIRECTIONS</c> 一一对应。</summary>
    public static string ToWireValue(this TranslationDirection direction) => direction switch
    {
        TranslationDirection.ChineseToEnglish => "zh-en",
        TranslationDirection.EnglishToChinese => "en-zh",
        TranslationDirection.ThaiToChinese => "th-zh",
        _ => throw new ArgumentOutOfRangeException(nameof(direction), direction, "未知的翻译方向")
    };

    /// <summary>
    /// 该方向实际会用到的 OPUS-MT 模型对。泰译中返回两段桥接路径。
    /// </summary>
    public static IReadOnlyList<string> ToModelPair(this TranslationDirection direction) => direction switch
    {
        TranslationDirection.ChineseToEnglish => ["zh-en"],
        TranslationDirection.EnglishToChinese => ["en-zh"],
        TranslationDirection.ThaiToChinese => ["th-en", "en-zh"],
        _ => throw new ArgumentOutOfRangeException(nameof(direction), direction, "未知的翻译方向")
    };

    public static bool TryParse(string? wireValue, out TranslationDirection direction)
    {
        switch (wireValue?.Trim().ToLowerInvariant())
        {
            case "zh-en": direction = TranslationDirection.ChineseToEnglish; return true;
            case "en-zh": direction = TranslationDirection.EnglishToChinese; return true;
            case "th-zh": direction = TranslationDirection.ThaiToChinese; return true;
            default: direction = default; return false;
        }
    }

    /// <summary>根据识别出的来源语言选出把它翻成中文的方向。</summary>
    public static TranslationDirection ToChineseFrom(string language, string text)
        => SpokenLanguage.Detect(text, language) == SpokenLanguage.Thai
            ? TranslationDirection.ThaiToChinese
            : TranslationDirection.EnglishToChinese;
}
