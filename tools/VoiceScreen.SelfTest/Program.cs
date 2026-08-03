using VoiceScreen.App.Audio;
using VoiceScreen.App.Models;
using VoiceScreen.App.Services;

Console.OutputEncoding = System.Text.Encoding.UTF8;
if (args.Any(arg => arg.Equals("--local-models", StringComparison.OrdinalIgnoreCase)))
{
    Console.WriteLine("VoiceScreen 纯本地双向模型自检");
    const string englishTest = "Enemies are on the second floor. Let's move to the left.";
    var englishPcm = await OfflineSpeech.SynthesizeEnglishAsync(englishTest, CancellationToken.None);
    await using var local = new LocalOutgoingService();
    await local.StartAsync(CancellationToken.None);

    var direct = await local.TranslateIncomingSpeechAsync(englishPcm, CancellationToken.None);
    Console.WriteLine($"PASS：本地英译中直连，{direct.SourceText} → {direct.TranslatedText}");
    if (string.IsNullOrWhiteSpace(direct.SourceText) || string.IsNullOrWhiteSpace(direct.TranslatedText))
        throw new InvalidOperationException("本地英译中没有返回完整结果。");

    await using var processor = new LocalIncomingAudioProcessor(local);
    var completed = new TaskCompletionSource<LocalIncomingTranslation>(TaskCreationOptions.RunContinuationsAsynchronously);
    processor.TranslationReady += (_, result) => completed.TrySetResult(result);
    processor.Error += (_, error) => completed.TrySetException(new InvalidOperationException(error));
    for (var offset = 0; offset < englishPcm.Length; offset += 1280)
    {
        var frame = new byte[1280];
        Buffer.BlockCopy(englishPcm, offset, frame, 0, Math.Min(1280, englishPcm.Length - offset));
        await processor.AddFrameAsync(frame, true, CancellationToken.None);
    }
    for (var i = 0; i < 14; i++)
        await processor.AddFrameAsync(new byte[1280], true, CancellationToken.None);

    var segmented = await completed.Task.WaitAsync(TimeSpan.FromSeconds(30));
    Console.WriteLine($"PASS：本地 VAD 分句英译中，{segmented.SourceText} → {segmented.TranslatedText}");
    return;
}

Console.WriteLine("VoiceScreen 本机音频自检");
var devices = new AudioDeviceService();
var captures = devices.GetCaptureDevices();
var renders = devices.GetRenderDevices();
foreach (var device in captures) Console.WriteLine($"[录音] {device.Name}");
foreach (var device in renders) Console.WriteLine($"[播放] {device.Name}");

var microphone = AudioDeviceService.FindBest(captures, string.Empty, "HyperX", "麦克风", "Microphone")
    ?? throw new InvalidOperationException("没有找到实体麦克风。");
var cable = AudioDeviceService.FindBest(renders, string.Empty, "CABLE Input")
    ?? throw new InvalidOperationException("没有找到 CABLE Input。");

Console.WriteLine($"选择麦克风：{microphone.Name}");
Console.WriteLine($"选择虚拟播放端：{cable.Name}");

using (var router = new MicrophoneCableRouter())
{
    router.Start(microphone.Id, cable.Id);
    Console.WriteLine($"路由已启动，麦克风格式：{router.MicrophoneFormat}");
    await Task.Delay(600);

    router.BeginTranslationCapture();
    await Task.Delay(800);
    var captured = router.EndTranslationCapture();
    Console.WriteLine($"录音自检：{captured.Data.Length} 字节，{captured.Duration.TotalMilliseconds:F0} ms");
    if (captured.Data.Length == 0) throw new InvalidOperationException("麦克风没有返回任何数据。");

    const string phrase = "Voice Screen self test. Virtual microphone routing is working.";
    var pcm = await OfflineSpeech.SynthesizeEnglishAsync(phrase, CancellationToken.None);
    Console.WriteLine($"离线英语 TTS：{pcm.Length} 字节");
    if (pcm.Length == 0) throw new InvalidOperationException("离线 TTS 没有生成音频。");
    await router.PlayTtsAsync(pcm, CancellationToken.None);
    router.RestorePassThrough();
}
Console.WriteLine("PASS：HyperX → 程序 → VB-CABLE 路由、录音切换、离线 TTS 均正常。");

var settings = new AppSettings
{
    DemoMode = true,
    MicrophoneDeviceId = microphone.Id,
    CableRenderDeviceId = cable.Id
};
var lines = new List<string>();
await using (var engine = new TranslationEngine(settings))
{
    engine.SubtitleProduced += (_, item) =>
    {
        lines.Add(item.Text);
        Console.WriteLine($"[字幕] {item.Text}");
    };
    engine.Error += (_, error) => throw new InvalidOperationException(error);
    await engine.StartAsync(CancellationToken.None);
    engine.BeginLocalCapture();
    await Task.Delay(800);
    await engine.EndLocalCaptureAsync();
    if (!engine.PassThroughEnabled) throw new InvalidOperationException("翻译结束后原声麦克风没有恢复。");
}
if (lines.Any(line => line.Contains("正在听你说中文", StringComparison.Ordinal)))
    throw new InvalidOperationException("录音状态不应写入永久字幕历史。");
if (!lines.Any(line => line.StartsWith("已发送：", StringComparison.Ordinal)))
    throw new InvalidOperationException("端到端模拟没有产生已发送英文字幕。");
Console.WriteLine("PASS：按键捕获 → 模拟翻译 → 英文 TTS → 恢复原声的完整状态链正常。");
