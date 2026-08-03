using VoiceScreen.Core;
using Xunit;

namespace VoiceScreen.Tests;

public sealed class DuplexStateMachineTests
{
    [Fact]
    public void CompletesExpectedOutgoingSequence()
    {
        var sut = new DuplexStateMachine();
        Assert.True(sut.TryBeginLocalCapture());
        Assert.False(sut.ShouldAcceptRemoteResult);
        Assert.True(sut.TryBeginTranslation());
        Assert.True(sut.TryBeginTts());
        Assert.True(sut.TryBeginCooldown());
        sut.Complete();
        Assert.Equal(DuplexState.Idle, sut.State);
        Assert.True(sut.ShouldAcceptRemoteResult);
    }

    [Fact]
    public void RejectsDuplicateCapture()
    {
        var sut = new DuplexStateMachine();
        Assert.True(sut.TryBeginLocalCapture());
        Assert.False(sut.TryBeginLocalCapture());
    }
}
