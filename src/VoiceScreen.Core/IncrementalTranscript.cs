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

    /// <summary>
    /// 流式 ASR 每次返回的是从句首开始的完整稳定前缀，而不是新增文本。模型还会在后续
    /// 快照里修正大小写、标点甚至早先的一两个词，因此不能用原始字符串的 StartsWith
    /// 判断增量：一次很小的修订就会把整段旧文本再次送去翻译。
    ///
    /// 先忽略空白和标点做精确前缀匹配；仍有词语修订时，再用词级编辑距离估计旧前缀
    /// 在新快照里的结束位置。两版差异过大时返回空串，等待下一帧重新建立基线；最终
    /// 快照仍会翻译完整句子，所以这里宁可暂缓，也不能制造重复译文。
    /// </summary>
    public static string NewlyStableSuffix(string? previousStable, string? currentStable)
    {
        var previous = (previousStable ?? string.Empty).Trim();
        var current = (currentStable ?? string.Empty).Trim();
        if (current.Length == 0) return string.Empty;
        if (previous.Length == 0) return current;

        var previousComparable = ComparableCharacters(previous);
        var currentComparable = ComparableCharacters(current);
        if (previousComparable.Length > 0
            && currentComparable.StartsWith(previousComparable, StringComparison.Ordinal))
        {
            var consumed = 0;
            for (var index = 0; index < current.Length; index++)
            {
                if (!char.IsLetterOrDigit(current[index])) continue;
                consumed++;
                if (consumed != previousComparable.Length) continue;
                return NextWordSuffix(current, index + 1);
            }
        }

        var oldWords = Tokenize(previous);
        var newWords = Tokenize(current);
        if (oldWords.Count == 0 || newWords.Count == 0) return string.Empty;

        var bestBoundary = 0;
        var bestDistance = int.MaxValue;
        for (var boundary = 1; boundary <= newWords.Count; boundary++)
        {
            var distance = EditDistance(oldWords, newWords, boundary);
            if (distance < bestDistance
                || distance == bestDistance
                && Math.Abs(boundary - oldWords.Count) < Math.Abs(bestBoundary - oldWords.Count))
            {
                bestDistance = distance;
                bestBoundary = boundary;
            }
        }

        var comparedLength = Math.Max(oldWords.Count, bestBoundary);
        var similarity = comparedLength == 0 ? 0 : 1d - bestDistance / (double)comparedLength;
        if (similarity < 0.75) return string.Empty;
        return bestBoundary >= newWords.Count
            ? string.Empty
            : current[newWords[bestBoundary].Start..].Trim();
    }

    private static string NextWordSuffix(string text, int start)
    {
        while (start < text.Length && !char.IsLetterOrDigit(text[start])) start++;
        return start < text.Length ? text[start..].Trim() : string.Empty;
    }

    private static string ComparableCharacters(string text)
    {
        Span<char> buffer = text.Length <= 512 ? stackalloc char[text.Length] : new char[text.Length];
        var length = 0;
        foreach (var character in text)
        {
            if (!char.IsLetterOrDigit(character)) continue;
            buffer[length++] = char.ToLowerInvariant(character);
        }
        return new string(buffer[..length]);
    }

    private static List<WordToken> Tokenize(string text)
    {
        var tokens = new List<WordToken>();
        var start = -1;
        for (var index = 0; index <= text.Length; index++)
        {
            var isWord = index < text.Length && (char.IsLetterOrDigit(text[index])
                                                  || text[index] == '\'' && start >= 0);
            if (isWord)
            {
                if (start < 0) start = index;
                continue;
            }

            if (start < 0) continue;
            var normalized = text[start..index].TrimEnd('\'').ToLowerInvariant();
            if (normalized.Length > 0) tokens.Add(new WordToken(normalized, start));
            start = -1;
        }
        return tokens;
    }

    private static int EditDistance(IReadOnlyList<WordToken> previous, IReadOnlyList<WordToken> current,
        int currentLength)
    {
        var row = new int[currentLength + 1];
        for (var column = 0; column <= currentLength; column++) row[column] = column;

        for (var previousIndex = 1; previousIndex <= previous.Count; previousIndex++)
        {
            var diagonal = row[0];
            row[0] = previousIndex;
            for (var currentIndex = 1; currentIndex <= currentLength; currentIndex++)
            {
                var above = row[currentIndex];
                var substitution = diagonal
                                   + (previous[previousIndex - 1].Text == current[currentIndex - 1].Text ? 0 : 1);
                row[currentIndex] = Math.Min(Math.Min(row[currentIndex] + 1, row[currentIndex - 1] + 1),
                    substitution);
                diagonal = above;
            }
        }
        return row[currentLength];
    }

    private sealed record WordToken(string Text, int Start);
}
