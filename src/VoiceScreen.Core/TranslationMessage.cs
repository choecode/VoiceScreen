namespace VoiceScreen.Core;

public enum AudioOrigin
{
    RemoteDiscordAudio,
    LocalMicrophone,
    GeneratedTts
}

public sealed record TranslationMessage(
    Guid Id,
    AudioOrigin Origin,
    string SourceLanguage,
    string TargetLanguage,
    string SourceText,
    string TranslatedText,
    DateTimeOffset CreatedAt,
    Guid? ParentMessageId = null);
