namespace VoiceScreen.Core;

/// <summary>
/// 发送方向的中文分句。
///
/// 一期是「松开右 Alt 之后才识别整句」，延迟等于说话时长加上整条流水线。分句抢跑的做法是：
/// 按住键的过程中就识别，一旦某个短句在标点处说完，立刻翻译并合成第一段英文送进 VB-CABLE，
/// 后面的中文继续识别。对方因此能提早一到三秒听到第一句。
///
/// 代价是已经播出去的英文收不回来，所以只有「稳定且完整」的短句才允许进队列：
/// 必须落在标点上，并且长度达到下限——把「嗯，」这种语气词单独合成一段没有意义，
/// 只会在对方耳朵里插入一段突兀的停顿。
/// </summary>
public static class ClauseSegmenter
{
    /// <summary>短于这个字数的片段不单独成句，并入下一段一起发。</summary>
    public const int MinimumClauseCharacters = 4;

    private static readonly char[] ClauseEndings =
        ['。', '！', '？', '，', '；', '、', '.', '!', '?', ',', ';'];

    /// <summary>
    /// 从稳定文本里取出 <paramref name="consumedCharacters"/> 之后所有已经说完的短句。
    /// </summary>
    /// <param name="stableText">LocalAgreement 判定稳定、不会再被改写的那段中文。</param>
    /// <param name="consumedCharacters">此前已经交给 TTS 的字符数。</param>
    public static ClauseSplit Split(string? stableText, int consumedCharacters)
    {
        var text = stableText ?? string.Empty;
        if (consumedCharacters < 0) consumedCharacters = 0;
        // 稳定前缀理论上只会变长，但识别结果回退时（例如换了一句话）要能自愈。
        if (consumedCharacters > text.Length) consumedCharacters = text.Length;

        var clauses = new List<string>();
        var consumed = consumedCharacters;
        var start = consumedCharacters;
        for (var index = consumedCharacters; index < text.Length; index++)
        {
            if (Array.IndexOf(ClauseEndings, text[index]) < 0) continue;
            var candidate = text[start..(index + 1)];
            // 长度按去掉标点后的实际内容算，否则「，，，」也会被当成一句。
            if (CountContentCharacters(candidate) < MinimumClauseCharacters) continue;
            clauses.Add(candidate.Trim());
            consumed = index + 1;
            start = consumed;
        }

        return new ClauseSplit(clauses, consumed);
    }

    /// <summary>
    /// 收尾时把剩下的尾巴取出来。松开按键后这一段无论有没有标点都必须发出去，
    /// 否则用户说的最后半句会被静默吞掉。
    /// </summary>
    public static string Remainder(string? fullText, int consumedCharacters)
    {
        var text = fullText ?? string.Empty;
        if (consumedCharacters <= 0) return text.Trim();
        if (consumedCharacters >= text.Length) return string.Empty;
        return text[consumedCharacters..].Trim();
    }

    /// <summary>
    /// 抢跑时至少要有这个比例的内容能和整句对上，才敢按内容切；对不上说明两次识别
    /// 分歧太大，只能退回按字数切。
    /// </summary>
    private const double MinimumAlignmentRatio = 0.6;

    /// <summary>
    /// 从「松手后重新识别的整句」里切掉「抢跑时已经播出去的那部分」。
    ///
    /// 抢跑用的是 base 临时模型，收尾用的是 small 定稿模型，两者对同一段音频给出的
    /// 字数并不相同，直接按已播字数下刀会切在错误的位置——多切一个字对方就漏听一个字，
    /// 少切一个字对方就听到重复。所以先按内容对齐：找出整句和已播文本的最长公共前缀，
    /// 在那里下刀。只有当两次识别分歧大到对不上时，才退回按字数切——
    /// 那种情况下宁可少说也不要让对方听到同一句话两遍。
    /// </summary>
    public static string RemainderAfterSpoken(string? fullText, string? spokenPrefix)
    {
        var text = fullText ?? string.Empty;
        var spoken = spokenPrefix ?? string.Empty;
        if (spoken.Trim().Length == 0) return text.Trim();

        var aligned = AlignedPrefixLength(text, spoken);
        var spokenContent = CountNonWhitespace(spoken);
        var matched = CountNonWhitespace(text[..aligned]);
        return matched >= spokenContent * MinimumAlignmentRatio
            ? text[aligned..].Trim()
            : Remainder(text, Math.Min(spoken.Length, text.Length));
    }

    /// <summary>整句里与已播文本逐字符一致的那段有多长（忽略空白）。</summary>
    private static int AlignedPrefixLength(string text, string spoken)
    {
        int textIndex = 0, spokenIndex = 0, lastMatch = 0;
        while (textIndex < text.Length && spokenIndex < spoken.Length)
        {
            if (char.IsWhiteSpace(text[textIndex])) { textIndex++; continue; }
            if (char.IsWhiteSpace(spoken[spokenIndex])) { spokenIndex++; continue; }
            if (char.ToUpperInvariant(text[textIndex]) != char.ToUpperInvariant(spoken[spokenIndex])) break;
            textIndex++;
            spokenIndex++;
            lastMatch = textIndex;
        }
        return lastMatch;
    }

    private static int CountNonWhitespace(string text)
        => text.Count(character => !char.IsWhiteSpace(character));

    private static int CountContentCharacters(string text)
        => text.Count(character => !char.IsWhiteSpace(character)
                                   && Array.IndexOf(ClauseEndings, character) < 0);
}

/// <param name="Clauses">本次新确认、可以立即翻译并播报的短句，按说话顺序排列。</param>
/// <param name="ConsumedCharacters">累计已交给 TTS 的字符数，下次调用要原样传回来。</param>
public sealed record ClauseSplit(IReadOnlyList<string> Clauses, int ConsumedCharacters);
