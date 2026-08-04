namespace VoiceScreen.App.Services;

public interface IIncomingAudioProcessor : IAsyncDisposable
{
    event EventHandler<LocalIncomingTranslation>? TranslationReady;
    event EventHandler<LocalIncomingTranslation?>? PreviewChanged;
    event EventHandler<string>? Error;
    event EventHandler<string>? Status;

    Task StartAsync(CancellationToken cancellationToken);
    ValueTask AddFrameAsync(byte[] frame, bool acceptAudio, CancellationToken cancellationToken);
}
