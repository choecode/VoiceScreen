using VoiceScreen.Core;
using Xunit;

namespace VoiceScreen.Tests;

public sealed class EchoSuppressorTests
{
    [Fact]
    public void DetectsRecentNormalizedEcho()
    {
        var now = DateTimeOffset.UtcNow;
        var sut = new EchoSuppressor();
        sut.RememberSent("They're on the second floor!", now);
        Assert.True(sut.IsLikelyEcho("theyre on the second floor", now.AddSeconds(3)));
    }

    [Fact]
    public void ExpiresOldMessages()
    {
        var now = DateTimeOffset.UtcNow;
        var sut = new EchoSuppressor(TimeSpan.FromSeconds(10));
        sut.RememberSent("Move to the left", now);
        Assert.False(sut.IsLikelyEcho("Move to the left", now.AddSeconds(11)));
    }

    [Fact]
    public void DoesNotRejectDifferentRemoteSpeech()
    {
        var sut = new EchoSuppressor();
        sut.RememberSent("I need ammunition");
        Assert.False(sut.IsLikelyEcho("Enemy behind the tree"));
    }
}
