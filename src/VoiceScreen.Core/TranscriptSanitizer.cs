namespace VoiceScreen.Core;

/// <summary>
/// 识别与翻译结果的病态输出检测。
///
/// Whisper 在静音段和低信噪比片段上会陷入循环，吐出「我去哪了我去哪了我去哪了……」
/// 这类周期性重复；OPUS-MT 收到这种输入后还会把它放大成几百字。这些字幕如果直接
/// 上屏，会把整块字幕区刷满。
///
/// 这些方法原先埋在 WPF 项目的 LocalOutgoingService 里，无法被单元测试覆盖——
/// 阈值调整全靠肉眼。移到 Core 后由 TranscriptSanitizerTests 固定行为。
/// </summary>
public static class TranscriptSanitizer
{
    /// <summary>单一字符占比超过这个比例就认为是卡住了（"啊啊啊啊啊……"）。</summary>
    private const double DominantSymbolRatio = 0.85;

    /// <summary>周期性重复的匹配率阈值，越高越保守，避免误杀正常的口语强调。</summary>
    private const double PeriodicMatchRatio = 0.88;

    /// <summary>低于这个字符数不做周期检测，短句里的重复通常是真实语气。</summary>
    private const int MinimumSymbolsForPeriodCheck = 16;

    /// <summary>单一词占比阈值（有空格的语言）。</summary>
    private const double DominantWordRatio = 0.7;

    private const int MinimumWordsForWordCheck = 6;

    /// <summary>译文相对原文的长度上限倍数，超过说明模型开始自由发挥。</summary>
    private const int MaximumExpansionFactor = 12;

    private const int MinimumExpansionAllowance = 120;

    public static bool IsPathologicalRepetition(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        var symbols = text.Where(char.IsLetterOrDigit)
            .Select(char.ToUpperInvariant)
            .ToArray();

        if (symbols.Length >= 2)
        {
            var dominantSymbols = symbols.GroupBy(character => character).Max(group => group.Count());
            if ((double)dominantSymbols / symbols.Length >= DominantSymbolRatio) return true;
        }

        // 中文没有空格，下面的按词统计对它无效，必须先做字符级的周期检测。
        if (symbols.Length >= MinimumSymbolsForPeriodCheck && HasPeriodicSymbolPattern(symbols)) return true;

        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(word => new string(word.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray()))
            .Where(word => word.Length > 0)
            .ToArray();

        if (words.Length < MinimumWordsForWordCheck) return false;
        var dominantWords = words.GroupBy(word => word).Max(group => group.Count());
        if ((double)dominantWords / words.Length >= DominantWordRatio) return true;

        return HasRepeatingPhrase(words);
    }

    /// <summary>
    /// 检测短语级复读。
    ///
    /// 单词频次统计漏掉了一整类真实幻觉：Whisper 在噪声下会输出
    /// "or a lightning or a lightning or lightning or lightning ..."，
    /// 由于 "or a lightning" 和 "or lightning" 交替出现，最高频单词只占约一半，
    /// 够不到 <see cref="DominantWordRatio"/>；字符级周期检测也会被这个变奏打断。
    /// 这段文本是 tools/benchmark_asr.py 在 10dB 信噪比下实测到的，24 词的音频
    /// 被识别成 223 词。
    ///
    /// 改为看「最高频 n 元词组覆盖了整句的多大比例」：真正的死循环里，
    /// 某个短语会铺满绝大部分句子，而正常口语不会。
    /// </summary>
    private static bool HasRepeatingPhrase(string[] words)
    {
        var maxLength = Math.Min(MaximumPhraseLength, words.Length / 3);
        for (var length = 2; length <= maxLength; length++)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            var highest = 0;
            for (var start = 0; start + length <= words.Length; start++)
            {
                var phrase = string.Join(' ', words, start, length);
                counts[phrase] = counts.TryGetValue(phrase, out var seen) ? seen + 1 : 1;
                if (counts[phrase] > highest) highest = counts[phrase];
            }

            // 至少重复三次才算循环，避免把"真的吗 真的吗"这种正常强调误判。
            if (highest < MinimumPhraseRepeats) continue;
            if ((double)(highest * length) / words.Length >= PhraseCoverageRatio) return true;
        }

        return false;
    }

    private const int MaximumPhraseLength = 5;
    private const int MinimumPhraseRepeats = 3;

    /// <summary>最高频短语要铺满整句的六成以上才判定为复读。</summary>
    private const double PhraseCoverageRatio = 0.6;

    /// <summary>译文异常膨胀或本身就是病态重复时，整句丢弃。</summary>
    public static bool IsUnsafeTranslation(string? source, string? translated)
    {
        if (string.IsNullOrEmpty(translated)) return false;
        var allowance = Math.Max(MinimumExpansionAllowance, (source?.Length ?? 0) * MaximumExpansionFactor);
        return translated.Length > allowance || IsPathologicalRepetition(translated);
    }

    /// <summary>
    /// 检测「短语被整段周期性复读」。逐个尝试周期长度，看有多少位置和一个周期前重合。
    /// </summary>
    private static bool HasPeriodicSymbolPattern(char[] symbols)
    {
        var maxPeriod = Math.Min(16, symbols.Length / 3);
        for (var period = 2; period <= maxPeriod; period++)
        {
            var matches = 0;
            for (var index = period; index < symbols.Length; index++)
            {
                if (symbols[index] == symbols[index % period]) matches++;
            }

            if ((double)matches / (symbols.Length - period) >= PeriodicMatchRatio) return true;
        }

        return false;
    }
}
