# VoiceScreen

VoiceScreen 是一个在 Windows 11 上运行的 Discord 双向语音翻译工具，运行时不依赖任何云端 API。

> 新电脑安装、模型下载、VB-CABLE/Discord 设置、离线验收和故障排查，请阅读 [完整部署教程](DEPLOYMENT.md)。
> 项目功能、界面、工作流程、日常使用与技术架构见 [项目说明](PROJECT_GUIDE.md)。
> 模型来源与许可见 [第三方模型说明](THIRD_PARTY_MODELS.md)。
> 一期之后的低延迟、增量字幕和实时翻译路线见 [二期实时化方案](REALTIME_ROADMAP.md)。

- 只捕获 Discord 进程的声音，不会把游戏、浏览器或系统声音送去识别。
- 对方说英语：本地 Whisper 识别英文，本地 OPUS-MT 翻译成中文；对方说泰语：使用 OPUS-MT `th-en → en-zh` 两段桥接翻译成中文；悬浮窗同时显示原文和译文。检测为中文时直接显示，不走翻译。
- 游戏低延迟字幕默认启用：Whisper base 生成临时结果，small 负责最终定稿；临时行原地更新，不写入历史，稳定模式可随时回退。
- 我方说中文：默认保持真实麦克风直通；按住右 Alt 录音，松开后本地生成英文字幕与英文语音，并通过 VB-CABLE 发送给 Discord。
- 可选择 Windows 已安装的英文男声/女声音色；“复读已发送英文”决定是否在实体耳机同步播放。
- “翻译并试听（仅耳机）”按钮可测试任意中文和当前音色，不会把测试音发进 Discord。
- 合成英文播放期间会暂停接收识别，结束后再恢复，可避免程序听见自己的声音而无限翻译。
- 悬浮窗保留最近 200 条字幕；运行时用 `PgUp` / `PgDn` 翻页，右上角会显示当前历史位置。主界面的“① 解锁移动/缩放”可解锁拖动和右下角缩放，完成后点“② 完成并锁定”恢复鼠标穿透。位置、尺寸和 `14–42` 字号都会自动保存。

## 本地技术栈

| 功能 | 实现 |
|---|---|
| 多语言语音识别 | faster-whisper `base` 实时预览 + `small` 最终定稿，CPU INT8 |
| 中英及泰中翻译 | Helsinki-NLP OPUS-MT 三个专用模型，泰语经 th-en → en-zh 桥接，CTranslate2 CPU INT8 |
| 英文语音合成 | Windows Speech 离线语音 |
| Discord 单独捕获 | Windows Process Loopback |
| 发送音频路由 | VB-Audio Virtual Cable |
| 桌面界面与悬浮窗 | .NET 8 / WPF |

模型下载完成后，运行期间只访问 `127.0.0.1` 上的本地服务，不上传语音、字幕或密钥。

## 首次安装

1. 安装 [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)。
2. 安装 Python 3.11。
3. 在源码目录运行一次模型准备脚本；发布目录中也会附带同名脚本：

   ```powershell
   powershell -ExecutionPolicy Bypass -File .\tools\setup_local_models.ps1
   ```

   脚本会下载 Whisper base/small，以及 OPUS-MT 中译英、英译中、泰译英三个官方模型，并转换为 CPU INT8 运行格式。

4. 安装 VB-Audio Virtual Cable。

## Discord 和程序设置

程序内：

- 实体麦克风：选择 HyperX 的真实麦克风。
- Discord 输出：程序会自动跟踪 Discord 进程，无须选整台电脑的扬声器。
- 虚拟麦克风播放端：选择 `CABLE Input (VB-Audio Virtual Cable)`。
- 英文试听耳机：选择 HyperX 实体耳机，并按需启用“发送英文时也在耳机播放”。
- 保持“只抓 Discord 进程音频”勾选。

Discord 的“语音和视频”设置：

- 麦克风：`CABLE Output (VB-Audio Virtual Cable)`。
- 扬声器：你的 HyperX 耳机。
- 不要把 Discord 扬声器设为 CABLE，否则容易形成回声或听不到对方。

启动 VoiceScreen 后，真实麦克风默认会被程序转发到 CABLE，所以正常说话仍是中文原声。按住右 Alt 时停止原声转发并录制中文；松开后生成英文，悬浮窗显示“我说”和“已发送”，英文语音随后送入 Discord，并可同步在耳机试听。播放期间麦克风直通与接收识别均暂停，缓冲排空后才恢复。

全局热键同时使用 Windows Raw Input 后台接收、低级键盘钩子和只读异步键状态轮询，并对三路事件去重。这样可提高带反作弊或 Raw Input 游戏前台运行时的兼容性；程序不注入游戏、不读取游戏内存。若游戏仍完全屏蔽后台按键读取，可在同等权限运行 VoiceScreen 后重试，或改用后续可配置组合键/鼠标侧键方案。

我方发送按阶段控制超时：本地中文识别和翻译最多等待 35 秒，英文语音合成及完整播放单独最多等待 60 秒。长英文播放时间不会再占用识别翻译的旧 10 秒预算；任何阶段失败仍会立即恢复原声麦克风。

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
- Discord Process Loopback 提供的是所有人的混合音频，因此当前不能显示真实说话人用户名；若增加本地说话人聚类，也只能标记“说话人 1/2”，多人重叠说话时不可靠。
- 第一次准备模型需要联网；准备完成后的翻译流程可以完全离线运行。
- 纯 CPU 方案优先减少显卡占用，实际延迟取决于 CPU；当前开发机预热后的完整发送链路约在 5 秒目标内。
- Discord 降噪可能裁掉 TTS 开头；程序已增加尾部静音和播放排空保护，仍建议在 Discord 中用“麦克风测试”确认一次。
