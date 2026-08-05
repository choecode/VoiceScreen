namespace VoiceScreen.Core;

/// <summary>Whisper 词级时间戳里的一个词。时间是相对于本次送进模型的那段音频的秒数。</summary>
public sealed record TranscribedWord(string Text, double Start, double End);

/// <summary>
/// 增量识别的滚动窗口计算。
///
/// 低延迟模式每 600ms 重跑一次识别。如果每次都把整句从头送进模型，计算量随句子长度
/// 平方增长——说到第 10 秒时每次都在重算那 10 秒，越说越慢。真正的解法是：
/// 已经确认（LocalAgreement 判定稳定）的那段前缀，连同它对应的那段音频一起从缓冲里删掉，
/// 之后只识别剩下的尾巴，窗口长度因此保持恒定。
///
/// 这个类负责唯一一件难以在 WPF 里测试的事：把「已确认的文本前缀」换算成
/// 「可以裁掉的音频秒数」。
/// </summary>
public static class TranscriptWindow
{
    /// <summary>
    /// 词级时间戳存在小幅提前的误差，裁剪点往回让出这么多秒，宁可多留一点音频，
    /// 也不要把下一个词的起始辅音切掉。
    /// </summary>
    public const double TrimSafetyMarginSeconds = 0.12;

    /// <summary>
    /// 找出 <paramref name="committedText"/> 覆盖到的最后一个词的结束时间。
    ///
    /// 词表是同一次识别结果拆出来的，所以把词按顺序拼起来必然能还原出整句文本；
    /// 只要逐词累加、在累加结果仍是已确认文本的前缀时继续，就能定位裁剪点。
    /// 比较前统一去掉空白并转小写：Whisper 的词自带前导空格，而稳定前缀是
    /// <see cref="IncrementalTranscript.LongestStablePrefix"/> TrimEnd 过的。
    /// </summary>
    /// <returns>可以裁掉的音频秒数；无法定位任何完整词时返回 0。</returns>
    public static double CommittedEndSeconds(IReadOnlyList<TranscribedWord>? words, string? committedText)
    {
        if (words is null || words.Count == 0) return 0;
        var target = Normalize(committedText);
        if (target.Length == 0) return 0;

        var matched = 0d;
        var consumed = 0;
        foreach (var word in words)
        {
            var normalized = Normalize(word.Text);
            if (normalized.Length == 0)
            {
                // 纯标点或纯空白的词不推进匹配，但它属于已确认部分，时间戳可以采用。
                if (consumed > 0) matched = Math.Max(matched, word.End);
                continue;
            }
            if (consumed + normalized.Length > target.Length) break;
            if (string.CompareOrdinal(target, consumed, normalized, 0, normalized.Length) != 0) break;
            consumed += normalized.Length;
            matched = Math.Max(matched, word.End);
        }

        return consumed == 0 ? 0 : Math.Max(0, matched - TrimSafetyMarginSeconds);
    }

    /// <summary>
    /// 裁掉已确认前缀后，上一次的识别假设也要跟着重新对齐到新窗口，否则
    /// LocalAgreement 会拿旧窗口的文本和新窗口的文本比较，得到一个必然为空的公共前缀。
    /// </summary>
    public static string RebasePreviousHypothesis(string? previous, string? committedText)
    {
        if (string.IsNullOrEmpty(previous)) return string.Empty;
        if (string.IsNullOrEmpty(committedText)) return previous;
        return previous.StartsWith(committedText, StringComparison.OrdinalIgnoreCase)
            ? previous[committedText.Length..].TrimStart()
            : string.Empty;
    }

    /// <summary>把已确认前缀和当前窗口的识别结果拼成完整的一句，用于上屏和最终定稿。</summary>
    public static string Join(string? committedText, string? windowText)
    {
        var left = (committedText ?? string.Empty).Trim();
        var right = (windowText ?? string.Empty).Trim();
        if (left.Length == 0) return right;
        if (right.Length == 0) return left;
        // 中泰文没有词间空格，英文才需要补一个。
        var needsSpace = !char.IsWhiteSpace(left[^1])
                         && !SpokenLanguage.IsIdeographic(left[^1])
                         && !SpokenLanguage.IsIdeographic(right[0]);
        return needsSpace ? left + ' ' + right : left + right;
    }

    private static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        Span<char> buffer = text.Length <= 256 ? stackalloc char[text.Length] : new char[text.Length];
        var length = 0;
        foreach (var character in text)
        {
            if (char.IsWhiteSpace(character)) continue;
            buffer[length++] = char.ToLowerInvariant(character);
        }
        return new string(buffer[..length]);
    }
}
