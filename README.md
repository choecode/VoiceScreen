# VoiceScreen

VoiceScreen 是一个在 Windows 11 上运行的 Discord 双向语音翻译工具，运行时不依赖任何云端 API。

- 只捕获 Discord 进程的声音，不会把游戏、浏览器或系统声音送去识别。
- 对方说英语：本地 Whisper 识别英文，本地 Qwen 翻译成中文，悬浮窗同时显示原文和译文。
- 我方说中文：默认保持真实麦克风直通；按住右 Alt 录音，松开后本地生成英文字幕与英文语音，并通过 VB-CABLE 发送给 Discord。
- 合成英文播放期间会暂停接收识别，结束后再恢复，可避免程序听见自己的声音而无限翻译。

## 本地技术栈

| 功能 | 实现 |
|---|---|
| 中英文语音识别 | faster-whisper `small`，CPU INT8 |
| 中英双向翻译 | Ollama `qwen2.5:1.5b`，强制 CPU |
| 英文语音合成 | Windows Speech 离线语音 |
| Discord 单独捕获 | Windows Process Loopback |
| 发送音频路由 | VB-Audio Virtual Cable |
| 桌面界面与悬浮窗 | .NET 8 / WPF |

模型下载完成后，运行期间只访问 `127.0.0.1` 上的本地服务，不上传语音、字幕或密钥。

## 首次安装

1. 安装 [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)。
2. 安装 Python 3.11，然后安装本地识别依赖：

   ```powershell
   python -m pip install --index-url https://pypi.org/simple faster-whisper==1.2.1
   ```

3. 首次联网下载 Whisper 模型；下载完以后可以断网使用：

   ```powershell
   $env:HF_HUB_OFFLINE='0'
   python -c "from faster_whisper import WhisperModel; WhisperModel('small', device='cpu', compute_type='int8')"
   ```

4. 安装 [Ollama](https://ollama.com/)，下载翻译模型：

   ```powershell
   ollama pull qwen2.5:1.5b
   ```

5. 安装 VB-Audio Virtual Cable。

## Discord 和程序设置

程序内：

- 实体麦克风：选择 HyperX 的真实麦克风。
- Discord 输出：程序会自动跟踪 Discord 进程，无须选整台电脑的扬声器。
- 虚拟麦克风播放端：选择 `CABLE Input (VB-Audio Virtual Cable)`。
- 保持“只抓 Discord 进程音频”勾选。

Discord 的“语音和视频”设置：

- 麦克风：`CABLE Output (VB-Audio Virtual Cable)`。
- 扬声器：你的 HyperX 耳机。
- 不要把 Discord 扬声器设为 CABLE，否则容易形成回声或听不到对方。

启动 VoiceScreen 后，真实麦克风默认会被程序转发到 CABLE，所以正常说话仍是中文原声。按住右 Alt 时停止原声转发并录制中文；松开后生成英文，悬浮窗显示“我说”和“已发送”，英文语音随后送入 Discord。

## 开发和验证

```powershell
dotnet build VoiceScreen.sln -c Release
dotnet test tests\VoiceScreen.Tests\VoiceScreen.Tests.csproj -c Release --no-build
dotnet run --project tools\VoiceScreen.SelfTest\VoiceScreen.SelfTest.csproj -c Release -- --local-models
```

应用发布：

```powershell
dotnet publish src\VoiceScreen.App\VoiceScreen.App.csproj -c Release -r win-x64 --self-contained false -o dist\VoiceScreen-local-offline
```

## 隐私与限制

- 默认不保存原始录音和字幕历史。
- 不注入 Discord 或游戏，不读取进程内存和网络数据。
- 第一次准备模型需要联网；准备完成后的翻译流程可以完全离线运行。
- 纯 CPU 方案优先减少显卡占用，实际延迟取决于 CPU；当前开发机预热后的完整发送链路约在 5 秒目标内。
- Discord 降噪可能裁掉 TTS 开头；程序已增加尾部静音和播放排空保护，仍建议在 Discord 中用“麦克风测试”确认一次。
