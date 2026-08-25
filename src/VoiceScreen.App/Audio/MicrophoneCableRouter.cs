using System.IO;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using VoiceScreen.App.Diagnostics;
using VoiceScreen.Core;

namespace VoiceScreen.App.Audio;

public sealed class MicrophoneCableRouter : IDisposable
{
    private const int Pcm16Mono16KhzBytesPerSecond = 32000;
    private const int TtsTrailingSilenceMilliseconds = 650;
    private const int TtsChunkSilenceMilliseconds = 120;
    private const int WasapiDrainGuardMilliseconds = 180;
    private readonly object _recordingGate = new();
    private WasapiCapture? _capture;
    private WasapiOut? _output;
    private WasapiOut? _monitorOutput;
    private BufferedWaveProvider? _microphoneBuffer;
    private BufferedWaveProvider? _ttsBuffer;
    private BufferedWaveProvider? _monitorTtsBuffer;
    private VolumeSampleProvider? _microphoneVolume;
    private MemoryStream? _recording;
    private DateTimeOffset _recordingStarted;
    private bool _disposed;

    public bool IsRunning => _capture is not null;
    public bool IsPassThroughEnabled => _microphoneVolume?.Volume > 0.5f;
    public string? MicrophoneFormat => _capture?.WaveFormat.ToString();

    public void Start(string microphoneDeviceId, string cableRenderDeviceId, string? monitorRenderDeviceId = null)
    {
        if (IsRunning) return;
        using var enumerator = new MMDeviceEnumerator();
        var microphone = enumerator.GetDevice(microphoneDeviceId);
        var cable = enumerator.GetDevice(cableRenderDeviceId);
        var monitor = string.IsNullOrWhiteSpace(monitorRenderDeviceId)
            ? null
            : enumerator.GetDevice(monitorRenderDeviceId);

        // 安全底线：如果把输出选成实体耳机，程序会把用户麦克风实时回放给自己，产生明显回声。
        // 宁可拒绝启动，也不能把任意播放设备当成虚拟麦克风线路。
        if (!AudioDeviceClassifier.IsVirtualCableSendEndpoint(cable.FriendlyName))
        {
            throw new InvalidOperationException(
                "发送设备必须是 CABLE Input (VB-Audio Virtual Cable)。已阻止向实体耳机回放麦克风，以免产生回声。");
        }
        if (AudioDeviceClassifier.IsVirtualAudioDevice(microphone.FriendlyName))
            throw new InvalidOperationException("实体麦克风不能选择虚拟音频线，请选择真实麦克风。");
        if (monitor is not null && AudioDeviceClassifier.IsVirtualAudioDevice(monitor.FriendlyName))
            throw new InvalidOperationException("英文试听设备必须是实体耳机，不能选择 VB-Audio 虚拟音频端点。");

        VoiceScreenLog.Info(
            $"Audio router opening. microphone={microphone.FriendlyName} cable={cable.FriendlyName} monitor={monitor?.FriendlyName ?? "disabled"}");

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

        if (monitor is not null)
        {
            _monitorTtsBuffer = CreateTtsBuffer();
            var monitorStereo = new MonoToStereoSampleProvider(_monitorTtsBuffer.ToSampleProvider());
            var monitor48k = new WdlResamplingSampleProvider(monitorStereo, 48000);
            try
            {
                _monitorOutput = new WasapiOut(monitor, AudioClientShareMode.Shared, true, 80);
                _monitorOutput.Init(monitor48k);
            }
            catch (COMException ex)
            {
                throw DescribeAudioOpenFailure("本地监听输出", monitor.FriendlyName, ex);
            }
        }

        var mixer = new MixingSampleProvider(new ISampleProvider[] { _microphoneVolume, tts48k })
        {
            ReadFully = true
        };

        try
        {
            _output = new WasapiOut(cable, AudioClientShareMode.Shared, true, 80);
            _output.Init(mixer);
        }
        catch (COMException ex)
        {
            throw DescribeAudioOpenFailure("VB-CABLE 发送输出", cable.FriendlyName, ex);
        }

        _capture.DataAvailable += OnMicrophoneData;
        _capture.RecordingStopped += OnRecordingStopped;
        try
        {
            _output.Play();
            _monitorOutput?.Play();
            _capture.StartRecording();
        }
        catch (COMException ex)
        {
            throw DescribeAudioOpenFailure("麦克风采集", microphone.FriendlyName, ex);
        }
        VoiceScreenLog.Info("Audio router started in WASAPI shared mode.");
    }

    private static InvalidOperationException DescribeAudioOpenFailure(string role, string device, COMException error)
    {
        const int AudclntDeviceInUse = unchecked((int)0x8889000A);
        var reason = error.HResult == AudclntDeviceInUse
            ? "设备正被其他程序以独占模式占用。请关闭该程序的独占音频或换一个实体耳机后重试。"
            : $"Windows 音频接口返回 0x{error.HResult:X8}。";
        VoiceScreenLog.Error($"Opening audio endpoint failed. role={role} device={device}", error);
        return new InvalidOperationException($"无法打开{role}“{device}”：{reason}", error);
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

    /// <summary>
    /// 取一份「到目前为止录到的中文」的副本，不结束录音。分句抢跑靠它在用户还按着
    /// 右 Alt 的时候就开始识别；正式的 <see cref="EndTranslationCapture"/> 依旧拿到完整录音。
    /// </summary>
    public CapturedAudio? PeekTranslationCapture()
    {
        if (_capture is null) return null;
        lock (_recordingGate)
        {
            if (_recording is null) return null;
            return new CapturedAudio(_recording.ToArray(), _capture.WaveFormat,
                DateTimeOffset.UtcNow - _recordingStarted);
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

    public async Task PlayTtsAsync(byte[] pcm16Mono16Khz, CancellationToken cancellationToken,
        bool playOnMonitor = true)
    {
        var monitor = playOnMonitor && _monitorTtsBuffer is not null;
        EnqueueTts(pcm16Mono16Khz, sendToCable: true, playOnMonitor: monitor,
            replaceQueued: true, trailingSilenceMilliseconds: TtsTrailingSilenceMilliseconds);
        await WaitForTtsDrainAsync(sendToCable: true, playOnMonitor: monitor, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task PlayMonitorTtsAsync(byte[] pcm16Mono16Khz, CancellationToken cancellationToken)
    {
        if (_monitorTtsBuffer is null) throw new InvalidOperationException("没有配置英文试听耳机。");
        EnqueueTts(pcm16Mono16Khz, sendToCable: false, playOnMonitor: true,
            replaceQueued: true, trailingSilenceMilliseconds: TtsTrailingSilenceMilliseconds);
        await WaitForTtsDrainAsync(sendToCable: false, playOnMonitor: true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 把一段英文语音接在当前播放队列后面，不清空已排队的内容，也不等待播放完成。
    ///
    /// 分句抢跑要的就是这个：第一段英文一合成好就入队开始播，第二段还在合成。
    /// 段与段之间只补一小段静音，听起来是自然的句间停顿而不是断句。
    /// </summary>
    public void EnqueueTts(byte[] pcm16Mono16Khz, bool playOnMonitor)
        => EnqueueTts(pcm16Mono16Khz, sendToCable: true,
            playOnMonitor: playOnMonitor && _monitorTtsBuffer is not null, replaceQueued: false,
            trailingSilenceMilliseconds: TtsTrailingSilenceMilliseconds);

    /// <summary>
    /// 长句流水线专用：中间块只补很短的自然停顿，最后一块仍保留完整尾静音，
    /// 确保 Discord 的 VAD 不会裁掉末尾单词。
    /// </summary>
    public void EnqueueTtsChunk(byte[] pcm16Mono16Khz, bool playOnMonitor, bool isFinal)
        => EnqueueTts(pcm16Mono16Khz, sendToCable: true,
            playOnMonitor: playOnMonitor && _monitorTtsBuffer is not null, replaceQueued: false,
            trailingSilenceMilliseconds: isFinal ? TtsTrailingSilenceMilliseconds : TtsChunkSilenceMilliseconds);

    /// <summary>队列里还有没有没播完的英文语音。</summary>
    public bool HasPendingTts
        => (_ttsBuffer?.BufferedBytes ?? 0) > 0 || (_monitorTtsBuffer?.BufferedBytes ?? 0) > 0;

    private void EnqueueTts(byte[] pcm16Mono16Khz, bool sendToCable, bool playOnMonitor, bool replaceQueued,
        int trailingSilenceMilliseconds)
    {
        if (_ttsBuffer is null) throw new InvalidOperationException("音频路由尚未启动。");

        // Discord 的语音活动检测和 VB-CABLE/WASAPI 都存在尾部缓冲。
        // 在句尾追加静音，确保最后一个单词完整穿过设备缓冲后再恢复原始麦克风。
        var trailingSilence = new byte[Pcm16Mono16KhzBytesPerSecond * trailingSilenceMilliseconds / 1000];
        if (sendToCable)
        {
            if (replaceQueued) _ttsBuffer.ClearBuffer();
            _ttsBuffer.AddSamples(pcm16Mono16Khz, 0, pcm16Mono16Khz.Length);
            _ttsBuffer.AddSamples(trailingSilence, 0, trailingSilence.Length);
        }
        if (playOnMonitor && _monitorTtsBuffer is not null)
        {
            if (replaceQueued) _monitorTtsBuffer.ClearBuffer();
            _monitorTtsBuffer.AddSamples(pcm16Mono16Khz, 0, pcm16Mono16Khz.Length);
            _monitorTtsBuffer.AddSamples(trailingSilence, 0, trailingSilence.Length);
        }
    }

    /// <summary>等到队列里的英文语音全部送进声卡为止。</summary>
    public async Task WaitForTtsDrainAsync(bool sendToCable, bool playOnMonitor,
        CancellationToken cancellationToken)
    {
        if (_ttsBuffer is null) throw new InvalidOperationException("音频路由尚未启动。");

        // 超时按队列里实际剩余的时长算：分句抢跑时排队的可能是好几段，
        // 按单段长度估算会在最后一段还没播完时就把麦克风放回来。
        var queued = Math.Max(sendToCable ? _ttsBuffer.BufferedBytes : 0,
            playOnMonitor && _monitorTtsBuffer is not null ? _monitorTtsBuffer.BufferedBytes : 0);
        var expected = TimeSpan.FromSeconds((double)queued / Pcm16Mono16KhzBytesPerSecond);
        var deadline = DateTimeOffset.UtcNow + expected + TimeSpan.FromSeconds(2);
        while (((sendToCable && _ttsBuffer.BufferedBytes > 0)
                || (playOnMonitor && _monitorTtsBuffer is not null && _monitorTtsBuffer.BufferedBytes > 0))
               && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(25, cancellationToken).ConfigureAwait(false);

        // BufferedBytes 为零表示数据已交给 WASAPI，不代表硬件/虚拟线末端已经播放完。
        await Task.Delay(WasapiDrainGuardMilliseconds, cancellationToken).ConfigureAwait(false);
    }

    private static BufferedWaveProvider CreateTtsBuffer() => new(new WaveFormat(16000, 16, 1))
    {
        BufferDuration = TimeSpan.FromSeconds(30),
        DiscardOnBufferOverflow = false,
        ReadFully = true
    };

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
        try { _monitorOutput?.Stop(); } catch { }
        _monitorOutput?.Dispose();
        _recording?.Dispose();
        _capture = null;
        _output = null;
        _monitorOutput = null;
    }
}
