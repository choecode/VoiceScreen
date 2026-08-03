namespace VoiceScreen.Core;

public enum DuplexState
{
    Idle,
    ReceivingForeignSpeech,
    CapturingLocalChinese,
    TranslatingLocalText,
    SendingEnglishTts,
    Cooldown,
    Faulted
}

public sealed class DuplexStateMachine
{
    private readonly object _gate = new();
    private DuplexState _state = DuplexState.Idle;

    public DuplexState State
    {
        get { lock (_gate) return _state; }
    }

    public event EventHandler<DuplexState>? StateChanged;

    public bool TryBeginLocalCapture()
    {
        lock (_gate)
        {
            if (_state is not (DuplexState.Idle or DuplexState.ReceivingForeignSpeech)) return false;
            SetStateUnsafe(DuplexState.CapturingLocalChinese);
            return true;
        }
    }

    public bool TryBeginTranslation() => TryTransition(DuplexState.CapturingLocalChinese, DuplexState.TranslatingLocalText);
    public bool TryBeginTts() => TryTransition(DuplexState.TranslatingLocalText, DuplexState.SendingEnglishTts);
    public bool TryBeginCooldown() => TryTransition(DuplexState.SendingEnglishTts, DuplexState.Cooldown);

    public void Complete() => Force(DuplexState.Idle);
    public void Fault() => Force(DuplexState.Faulted);
    public void Reset() => Force(DuplexState.Idle);

    public bool ShouldAcceptRemoteResult => State is DuplexState.Idle or DuplexState.ReceivingForeignSpeech;

    private bool TryTransition(DuplexState expected, DuplexState next)
    {
        lock (_gate)
        {
            if (_state != expected) return false;
            SetStateUnsafe(next);
            return true;
        }
    }

    private void Force(DuplexState next)
    {
        lock (_gate) SetStateUnsafe(next);
    }

    private void SetStateUnsafe(DuplexState next)
    {
        if (_state == next) return;
        _state = next;
        StateChanged?.Invoke(this, next);
    }
}
