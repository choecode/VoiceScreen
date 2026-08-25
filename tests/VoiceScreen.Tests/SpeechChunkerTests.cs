using VoiceScreen.Core;
using Xunit;

namespace VoiceScreen.Tests;

public sealed class SpeechChunkerTests
{
    [Fact]
    public void ShortSentence_RemainsOneChunk()
    {
        var chunks = SpeechChunker.SplitEnglish("Hold this position and wait for me.");

        Assert.Equal(["Hold this position and wait for me."], chunks);
    }

    [Fact]
    public void LongText_PrefersSentenceThenPhraseBoundaries()
    {
        const string text =
            "Hold this position until the first team enters the warehouse. Then move to the right side, but do not cross the open field.";

        var chunks = SpeechChunker.SplitEnglish(text, preferredCharacters: 50, maximumCharacters: 70);

        Assert.Equal(2, chunks.Count);
        Assert.EndsWith(".", chunks[0]);
        Assert.Equal(Normalize(text), Normalize(string.Join(' ', chunks)));
        Assert.All(chunks, chunk => Assert.InRange(chunk.Length, 1, 70));
    }

    [Fact]
    public void NoPunctuation_SplitsOnlyAtWordBoundary()
    {
        const string text =
            "alpha bravo charlie delta echo foxtrot golf hotel india juliet kilo lima mike november oscar papa";

        var chunks = SpeechChunker.SplitEnglish(text, preferredCharacters: 32, maximumCharacters: 44);

        Assert.True(chunks.Count >= 2);
        Assert.Equal(Normalize(text), Normalize(string.Join(' ', chunks)));
        Assert.DoesNotContain(chunks, chunk => chunk.StartsWith(' ') || chunk.EndsWith(' '));
    }

    [Fact]
    public void RepeatedWhitespace_IsCollapsedWithoutLosingWords()
    {
        var chunks = SpeechChunker.SplitEnglish("  move   left\r\nthen   wait  ", 16, 20);

        Assert.Equal("move left then wait", string.Join(' ', chunks));
    }

    [Fact]
    public void InvalidLimits_AreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SpeechChunker.SplitEnglish("hello", 15, 30));
        Assert.Throws<ArgumentOutOfRangeException>(() => SpeechChunker.SplitEnglish("hello", 40, 39));
    }

    private static string Normalize(string value)
        => string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
