using System.Text;
using System.Text.RegularExpressions;

namespace VoiceScreen.Core;

public sealed partial class EchoSuppressor
{
    private readonly TimeSpan _window;
    private readonly int _capacity;
    private readonly Queue<(string Normalized, DateTimeOffset SentAt)> _sent = new();
    private readonly object _gate = new();

    public EchoSuppressor(TimeSpan? window = null, int capacity = 20)
    {
        _window = window ?? TimeSpan.FromSeconds(15);
        _capacity = capacity;
    }

    public void RememberSent(string text, DateTimeOffset? now = null)
    {
        var normalized = Normalize(text);
        if (normalized.Length == 0) return;
        lock (_gate)
        {
            _sent.Enqueue((normalized, now ?? DateTimeOffset.UtcNow));
            while (_sent.Count > _capacity) _sent.Dequeue();
        }
    }

    public bool IsLikelyEcho(string text, DateTimeOffset? now = null)
    {
        var normalized = Normalize(text);
        if (normalized.Length == 0) return false;
        var timestamp = now ?? DateTimeOffset.UtcNow;
        lock (_gate)
        {
            while (_sent.Count > 0 && timestamp - _sent.Peek().SentAt > _window) _sent.Dequeue();
            return _sent.Any(item => Similarity(item.Normalized, normalized) >= 0.86);
        }
    }

    public static string Normalize(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text.ToLowerInvariant().Normalize(NormalizationForm.FormKC))
        {
            if (char.IsLetterOrDigit(c) || char.IsWhiteSpace(c)) builder.Append(c);
        }
        return WhitespaceRegex().Replace(builder.ToString(), " ").Trim();
    }

    internal static double Similarity(string left, string right)
    {
        if (left == right) return 1;
        if (left.Length == 0 || right.Length == 0) return 0;
        var distance = Levenshtein(left, right);
        return 1d - (double)distance / Math.Max(left.Length, right.Length);
    }

    private static int Levenshtein(string left, string right)
    {
        var previous = Enumerable.Range(0, right.Length + 1).ToArray();
        var current = new int[right.Length + 1];
        for (var i = 1; i <= left.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= right.Length; j++)
            {
                var cost = left[i - 1] == right[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }
            (previous, current) = (current, previous);
        }
        return previous[right.Length];
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
