namespace VoiceScreen.Core;

/// <summary>
/// 把较长的英文翻译切成适合低延迟 TTS 的自然片段。已经播放的语音无法撤回，
/// 所以这里只切完整翻译，不参与尚未稳定的 ASR/翻译提交判断。
/// </summary>
public static class SpeechChunker
{
    public const int DefaultPreferredCharacters = 60;
    public const int DefaultMaximumCharacters = 80;

    public static IReadOnlyList<string> SplitEnglish(string? text,
        int preferredCharacters = DefaultPreferredCharacters,
        int maximumCharacters = DefaultMaximumCharacters)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<string>();
        if (preferredCharacters < 16)
            throw new ArgumentOutOfRangeException(nameof(preferredCharacters));
        if (maximumCharacters < preferredCharacters)
            throw new ArgumentOutOfRangeException(nameof(maximumCharacters));

        var remaining = CollapseWhitespace(text);
        var chunks = new List<string>();
        while (remaining.Length > maximumCharacters)
        {
            var split = FindSplit(remaining, preferredCharacters, maximumCharacters);
            var chunk = remaining[..split].Trim();
            if (chunk.Length == 0)
                split = Math.Min(maximumCharacters, remaining.Length);
            else
                chunks.Add(chunk);
            remaining = remaining[split..].TrimStart();
        }

        if (remaining.Length > 0) chunks.Add(remaining);
        return chunks;
    }

    private static int FindSplit(string text, int preferred, int maximum)
    {
        var minimum = Math.Max(12, preferred / 2);

        // 先找完整句子。尽量靠近目标长度，但允许在硬上限之前继续找到更自然的句尾。
        var sentence = LastBoundary(text, minimum, maximum, IsSentenceBoundary);
        if (sentence > 0) return sentence;

        // 没有句尾时在从句/短语边界切，避免七八十字符全部送进一次 TTS。
        var phrase = LastBoundary(text, minimum, maximum, IsPhraseBoundary);
        if (phrase > 0) return phrase;

        // 最后只在单词间切。极长 URL/标识符没有空格时才使用硬上限。
        var whitespace = LastBoundary(text, minimum, maximum, char.IsWhiteSpace);
        return whitespace > 0 ? whitespace : maximum;
    }

    private static int LastBoundary(string text, int minimum, int maximum, Func<char, bool> predicate)
    {
        for (var index = Math.Min(maximum, text.Length - 1); index >= minimum; index--)
        {
            if (!predicate(text[index])) continue;
            // 标点属于前一个片段；空格留给 Trim 处理。
            return char.IsWhiteSpace(text[index]) ? index : index + 1;
        }
        return -1;
    }

    private static bool IsSentenceBoundary(char value) => value is '.' or '?' or '!';
    private static bool IsPhraseBoundary(char value) => value is ',' or ';' or ':';

    private static string CollapseWhitespace(string value)
        => string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
