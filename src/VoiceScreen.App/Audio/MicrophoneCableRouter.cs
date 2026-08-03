using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace VoiceScreen.App.Audio;

public sealed class MicrophoneCableRouter : IDisposable
{
    private const int Pcm16Mono16KhzBytesPerSecond = 32000;
    private const int TtsTrailingSilenceMilliseconds = 650;
    private const int WasapiDrainGuardMilliseconds = 180;
    private readonly object _recordingGate = new();
    private WasapiCapture? _capture;
    private WasapiOut? _output;
    private BufferedWaveProvider? _microphoneBuffer;
    private BufferedWaveProvider? _ttsBuffer;
    private VolumeSampleProvider? _microphoneVolume;
    private MemoryStream? _recording;
    private DateTimeOffset _recordingStarted;
    private bool _disposed;

    public bool IsRunning => _capture is not null;
    public bool IsPassThroughEnabled => _microphoneVolume?.Volume > 0.5f;
    public string? MicrophoneFormat => _capture?.WaveFormat.ToString();

    public void Start(string microphoneDeviceId, string cableRenderDeviceId)
    {
        if (IsRunning) return;
        using var enumerator = new MMDeviceEnumerator();
        var microphone = enumerator.GetDevice(microphoneDeviceId);
        var cable = enumerator.GetDevice(cableRenderDeviceId);

        // 安全底线：如果把输出选成实体耳机，程序会把用户麦克风实时回放给自己，产生明显回声。
        // 宁可拒绝启动，也不能把任意播放设备当成虚拟麦克风线路。
        if (!cable.FriendlyName.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase)
            || !cable.FriendlyName.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "发送设备必须是 CABLE Input (VB-Audio Virtual Cable)。已阻止向实体耳机回放麦克风，以免产生回声。");
        }
        if (microphone.FriendlyName.Contains("CABLE Output", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("实体麦克风不能选择 CABLE Output，请选择 HyperX 麦克风。");

        _capture = new WasapiCapture(microphone, true, 40);
        _microphoneBuffer = new BufferedWaveProvider(_capture.WaveFormat)
        {
            BufferDuration = TimeSpan.FromSeconds(5),
            DiscardOnBufferOverflow = true,
            ReadFully = true
        };

        var microphoneStereo = SampleProviderChannels.ToStereo(_microphoneBuffer.ToSampleProvider());
        var microphone48k = new WdlResamplingSampleProvider(microphoneStereo, 48000);
        _microphoneVolume = new VolumeSampleProvider(microphone48k) { Volume = 1f };

        _ttsBuffer = new BufferedWaveProvider(new WaveFormat(16000, 16, 1))
        {
            BufferDuration = TimeSpan.FromSeconds(30),
            DiscardOnBufferOverflow = false,
            ReadFully = true
        };
        var ttsStereo = new MonoToStereoSampleProvider(_ttsBuffer.ToSampleProvider());
        var tts48k = new WdlResamplingSampleProvider(ttsStereo, 48000);

        var mixer = new MixingSampleProvider(new ISampleProvider[] { _microphoneVolume, tts48k })
        {
            ReadFully = true
        };

        _output = new WasapiOut(cable, AudioClientShareMode.Shared, true, 80);
        _output.Init(mixer);

        _capture.DataAvailable += OnMicrophoneData;
        _capture.RecordingStopped += OnRecordingStopped;
        _output.Play();
        _capture.StartRecording();
    }

    public void BeginTranslationCapture()
    {
        if (_capture is null || _microphoneVolume is null) throw new InvalidOperationException("音频路由尚未启动。");
        lock (_recordingGate)
        {
            _recording?.Dispose();
            _recording = new MemoryStream();
            _recordingStarted = DateTimeOffset.UtcNow;
            _microphoneVolume.Volume = 0f;
        }
    }

    public CapturedAudio EndTranslationCapture()
    {
        if (_capture is null) throw new InvalidOperationException("音频路由尚未启动。");
        lock (_recordingGate)
        {
            var stream = _recording;
            _recording = null;
            var data = stream?.ToArray() ?? Array.Empty<byte>();
            stream?.Dispose();
            return new CapturedAudio(data, _capture.WaveFormat, DateTimeOffset.UtcNow - _recordingStarted);
        }
    }

    public void RestorePassThrough()
    {
        if (_microphoneVolume is not null) _microphoneVolume.Volume = 1f;
        lock (_recordingGate)
        {
            _recording?.Dispose();
            _recording = null;
        }
    }

    public async Task PlayTtsAsync(byte[] pcm16Mono16Khz, CancellationToken cancellationToken)
    {
        if (_ttsBuffer is null) throw new InvalidOperationException("音频路由尚未启动。");
        _ttsBuffer.ClearBuffer();
        _ttsBuffer.AddSamples(pcm16Mono16Khz, 0, pcm16Mono16Khz.Length);

        // Discord 的语音活动检测和 VB-CABLE/WASAPI 都存在尾部缓冲。
        // 在句尾追加静音，确保最后一个单词完整穿过设备缓冲后再恢复原始麦克风。
        var trailingSilence = new byte[Pcm16Mono16KhzBytesPerSecond * TtsTrailingSilenceMilliseconds / 1000];
        _ttsBuffer.AddSamples(trailingSilence, 0, trailingSilence.Length);

        var expected = TimeSpan.FromSeconds(
            (double)(pcm16Mono16Khz.Length + trailingSilence.Length) / Pcm16Mono16KhzBytesPerSecond);
        var deadline = DateTimeOffset.UtcNow + expected + TimeSpan.FromSeconds(2);
        while (_ttsBuffer.BufferedBytes > 0 && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(25, cancellationToken).ConfigureAwait(false);

        // BufferedBytes 为零表示数据已交给 WASAPI，不代表硬件/虚拟线末端已经播放完。
        await Task.Delay(WasapiDrainGuardMilliseconds, cancellationToken).ConfigureAwait(false);
    }

    private void OnMicrophoneData(object? sender, WaveInEventArgs e)
    {
        _microphoneBuffer?.AddSamples(e.Buffer, 0, e.BytesRecorded);
        lock (_recordingGate) _recording?.Write(e.Buffer, 0, e.BytesRecorded);
    }

    private static void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null) System.Diagnostics.Debug.WriteLine(e.Exception);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_capture is not null)
        {
            _capture.DataAvailable -= OnMicrophoneData;
            _capture.RecordingStopped -= OnRecordingStopped;
            try { _capture.StopRecording(); } catch { }
            _capture.Dispose();
        }
        try { _output?.Stop(); } catch { }
        _output?.Dispose();
        _recording?.Dispose();
        _capture = null;
        _output = null;
    }
}
