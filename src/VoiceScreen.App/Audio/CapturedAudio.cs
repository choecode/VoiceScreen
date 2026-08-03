using NAudio.Wave;

namespace VoiceScreen.App.Audio;

public sealed record CapturedAudio(byte[] Data, WaveFormat Format, TimeSpan Duration);
