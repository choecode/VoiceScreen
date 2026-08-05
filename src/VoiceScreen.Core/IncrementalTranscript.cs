namespace VoiceScreen.Core;

/// <summary>
/// 增量 ASR 的稳定前缀提取。
///
/// 低延迟模式每 600ms 对同一句话重跑一次识别，后一次的结果会修改前一次的尾部。
/// 只有连续两次假设都一致的前缀才算「稳定」，可以送去翻译并上屏；否则字幕会
/// 一直闪烁重写。英文还要退到词边界，避免把半个单词丢给 OPUS-MT。
///
/// 和 <see cref="TranscriptSanitizer"/> 一样，这段逻辑原先锁在 WPF 项目里无法测试。
/// </summary>
public static class IncrementalTranscript
{
    private static readonly char[] EnglishBoundaries = [' ', ',', '.', '!', '?', ';', ':'];

    private static readonly char[] ClauseEndings = ['.', '!', '?', '。', '！', '？'];

    /// <summary>
    /// 取两次识别假设的公共前缀。英文在词边界处截断，中泰按字符截断。
    /// </summary>
    public static string LongestStablePrefix(string? previous, string? current, string? language)
    {
        if (string.IsNullOrEmpty(previous) || string.IsNullOrEmpty(current)) return string.Empty;

        var length = Math.Min(previous.Length, current.Length);
        var index = 0;
        while (index < length && char.ToUpperInvariant(previous[index]) == char.ToUpperInvariant(current[index]))
            index++;
        if (index == 0) return string.Empty;

        var prefix = current[..index].TrimEnd();
        if (SpokenLanguage.Is(language, SpokenLanguage.English))
        {
            var boundary = prefix.LastIndexOfAny(EnglishBoundaries);
            // boundary == 0 时整个前缀就是一个分隔符，截了会得到空串，不如整体放弃。
            prefix = boundary > 0 ? prefix[..boundary].TrimEnd() : string.Empty;
        }

        return prefix;
    }

    /// <summary>
    /// 稳定前缀短于这个长度就不值得翻译——太短的片段缺乏上下文，OPUS-MT 容易乱猜。
    /// 泰文没有词间空格，单位信息量低，门槛相应抬高。
    /// </summary>
    public static int MinimumStableLength(string? language)
        => SpokenLanguage.Is(language, SpokenLanguage.Thai) ? 4 : 3;

    /// <summary>句末标点意味着这一段已经说完，可以直接当稳定结果用。</summary>
    public static bool EndsClause(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        return Array.IndexOf(ClauseEndings, text[^1]) >= 0;
    }
}
