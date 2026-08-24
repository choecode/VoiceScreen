namespace VoiceScreen.Core;

/// <summary>
/// 实时字幕的语义分段门控。AI 只在文本已经积累到足够上下文时参与判断；真正收尾还要
/// 等到一个很短的声音停顿，避免模型在说话正中间把音频切开。
/// </summary>
public static class SubtitleBoundaryPolicy
{
    public const int MinimumDecisionFrames = 75; // 3s
    public const int DecisionIntervalFrames = 50; // 2s
    public const int SemanticPauseFrames = 3; // 120ms
    public const int MaximumDecisionAgeFrames = 50; // 2s

    public static bool ShouldRequestSemanticDecision(string? text, int totalFrames, int lastDecisionFrame)
    {
        var value = text?.Trim() ?? string.Empty;
        if (totalFrames < MinimumDecisionFrames
            || totalFrames - lastDecisionFrame < DecisionIntervalFrames
            || value.Length < 32)
            return false;

        // 有句末标点时让语义模型确认它是不是 ASR 临时补出的假句号；没有标点但已经很长时，
        // 也给模型一次寻找自然短句边界的机会。
        return IncrementalTranscript.EndsClause(value) || value.Length >= 80;
    }

    public static bool ShouldCompleteAtSemanticPause(bool boundaryReady, int silenceFrames,
        int totalFrames, int boundaryFrame)
        => boundaryReady
           && silenceFrames >= SemanticPauseFrames
           && boundaryFrame > 0
           && totalFrames - boundaryFrame <= MaximumDecisionAgeFrames;
}
