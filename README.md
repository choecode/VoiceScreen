# VoiceScreen

VoiceScreen 是一个在 Windows 11 上运行的 Discord 双向语音翻译工具。它不依赖局域网服务器，也不使用科大讯飞或付费密钥。

ASR 固定使用本机 Whisper；文本翻译和英文 TTS 可分别选择本地或免费 API，并且能在运行中独立切换，下一句话立即生效。纯本地组合完全离线；API 组合使用 MyMemory 翻译和 Edge TTS。

> 新电脑安装、模型下载、VB-CABLE/Discord 设置、离线验收和故障排查，请阅读 [完整部署教程](DEPLOYMENT.md)。
> 项目功能、界面、工作流程、日常使用与技术架构见 [项目说明](PROJECT_GUIDE.md)。
> 模型来源与许可见 [第三方模型说明](THIRD_PARTY_MODELS.md)。
> 一期之后的低延迟、增量字幕和实时翻译路线见 [二期实时化方案](REALTIME_ROADMAP.md)。

- 只捕获 Discord 进程的声音，不会把游戏、浏览器或系统声音送去识别。
- 对方说英语：本机 Whisper 识别英文，再按当前选择使用本地 OPUS-MT 或 MyMemory API 翻译成中文；泰语同样支持。悬浮窗同时显示原文和译文，检测为中文时直接显示。
- 游戏低延迟字幕默认启用：Whisper base 生成临时结果，small 负责最终定稿；临时行原地更新，不写入历史，稳定模式可随时回退。
- 我方说中文：默认保持真实麦克风直通；按住右 Alt 录音，松开后本地生成英文字幕与英文语音，并通过 VB-CABLE 发送给 Discord。
- 英文 TTS 可选本地 Windows Speech 或 Edge TTS API，并提供男女音色；“复读已发送英文”决定是否在实体耳机同步播放。
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

语音始终只在本机识别。选择本地翻译和本地 TTS 时不会上传语音或文字；选择 API 时，识别出的文字会发给 MyMemory，英文译文会发给 Edge TTS。

### 项目结构

| 项目 | 职责 |
|---|---|
| `src/VoiceScreen.Core` | 与平台无关的纯逻辑：翻译方向、语种判定、病态输出检测、增量字幕稳定前缀、PCM 电平。全部有单元测试覆盖。 |
| `src/VoiceScreen.App` | WPF 界面、音频路由、Discord 捕获、本地服务进程管理。 |
| `src/VoiceScreen.App/LocalService` | Python 推理服务：Whisper/Sherpa ASR + OPUS-MT 翻译 + 评测台 HTTP 接口。 |
| `tools/VoiceScreen.SelfTest` | 需要真实模型的端到端自检。 |

**翻译方向只有一处定义**：`TranslationDirection` 区分「用户方向」（`zh-en` / `en-zh` /
`th-zh`，即 HTTP 契约和界面暴露的）和「模型对」（`zh-en` / `en-zh` / `th-en`，即实际存在的
OPUS-MT 模型）。泰译中没有直接模型，`ToModelPair()` 会返回 `th-en → en-zh` 两段桥接路径，
Python 侧的 `USER_DIRECTIONS` / `MODEL_PAIRS` 与之一一对应。

**语种判定只有一处实现**：`SpokenLanguage.Detect` 以文本字符分布为准，ASR 报告的语种标签
只作为回退——Whisper 在短句上的 `language` 字段并不可靠。Python 侧的 `detect_language`
使用同一套 Unicode 区间。

## 浏览器翻译质量评测台

本地模型服务启动后，浏览器打开：

```text
http://127.0.0.1:18765/
```

评测台不依赖 Discord、WPF、麦克风或 VB-CABLE，可以直接测试：

- 中文 → 英文、英文 → 中文、泰语 → 英文 → 中文；
- 原始 OPUS-MT 与游戏术语规则增强的并排结果；
- beam size、最大解码长度、单次推理耗时与泰语桥接英文；
- 1–5 分质量评分、期望译文、错误标签和场景备注；
- 浏览器本地评测集，以及 JSONL / CSV 导出。

评测台还提供两条 TTS 对照链路：服务器可选本地 Piper（当前运行时 GPL-3.0；各音色的数据集许可证不同，并由 `/providers` 的 `voiceLicenses` 分项披露；该 provider 不进入 Windows 默认依赖）以及 MyMemory 公共翻译接口 + Microsoft Edge 在线 TTS。选择在线 provider 时，页面会明确提示数据将发送给第三方；可以分别查看翻译耗时、TTS 首包/总耗时、音频时长、实时率 RTF、整条流水线耗时、字符吞吐与译文长度比，并直接播放合成音频。免费公共接口没有 SLA，存在额度、限流和策略变更风险，只用于质量对照，不应作为生产依赖。

Windows 客户端可独立选择翻译与 TTS 引擎，共有四种组合：本地/本地、API/本地、本地/API、API/API。两类音色配置互不混用。

评分数据默认只保存在当前浏览器的 `localStorage`，不会写入服务端或上传。导出的 JSONL 可以继续作为固定回归语料、术语表输入或后续微调数据源。

只测试翻译模型、不加载 Whisper 时，可跨平台直接启动：

```bash
VOICESCREEN_MODEL_ROOT=/path/to/VoiceScreen/Models \
python src/VoiceScreen.App/LocalService/local_outgoing_service.py --translation-only
```

模型目录需要包含 setup 脚本生成的三个 `opus-mt-*-ct2-int8` 目录。Windows 下也可使用 `--model-root` 显式指定模型目录。

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

全部测试（C# 68 个 + Python 30 个）走同一条命令。两套测试对工作目录的要求不同，
脚本已经封装好，本地和 CI 用的是同一个入口：

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\run_tests.ps1
```

只跑其中一侧：

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\run_tests.ps1 -DotnetOnly
```

源码编码检查（几秒钟，CI 里排在最前面）：

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\check_encoding.ps1
```

它会拦住两类曾经真实发生过的问题：源码文件里出现非法 UTF-8 序列（中文常量被截断，
Python 直接 SyntaxError 起不来），以及含中文的 `.ps1` 缺少 UTF-8 BOM
（Windows PowerShell 5.1 会按 ANSI 解码并报 ParserError）。

需要真实模型的端到端自检：

```powershell
dotnet run --project tools\VoiceScreen.SelfTest\VoiceScreen.SelfTest.csproj -c Release -- --local-models
```

`.github/workflows/ci.yml` 在 push 和 PR 上跑「编码检查 → 构建 → 全部测试」。
Python 用例全部使用桩模块，不需要安装模型运行时。

发布步骤见 [完整部署教程](DEPLOYMENT.md)。

## 隐私与限制

- 默认不保存原始录音和字幕历史。
- 不注入 Discord 或游戏，不读取进程内存和网络数据。
- Discord Process Loopback 提供的是所有人的混合音频，因此当前不能显示真实说话人用户名；若增加本地说话人聚类，也只能标记“说话人 1/2”，多人重叠说话时不可靠。
- 第一次准备模型需要联网；准备完成后的翻译流程可以完全离线运行。
- 纯 CPU 方案优先减少显卡占用，实际延迟取决于 CPU；当前开发机预热后的完整发送链路约在 5 秒目标内。
- Discord 降噪可能裁掉 TTS 开头；程序已增加尾部静音和播放排空保护，仍建议在 Discord 中用“麦克风测试”确认一次。

## 可选的 ASR 引擎

默认使用 faster-whisper（`base` 实时预览 + `small` 最终定稿），支持中/英/泰三语。

也可以切到 Sherpa-ONNX Zipformer。它**不在默认安装范围内**，需要显式装一次
（Python 包 + 约 190 MB 模型文件，一条命令装齐）：

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\setup_local_models.ps1 -Sherpa
```

脚本会下载 [csukuangfj/sherpa-onnx-streaming-zipformer-bilingual-zh-en-2023-02-20](https://huggingface.co/csukuangfj/sherpa-onnx-streaming-zipformer-bilingual-zh-en-2023-02-20)
的 INT8 版本，装完会立刻加载一次做验证，装坏了当场就报错，而不是等到应用启动时
给一句看不懂的提示。

> **Sherpa 只支持中文和英文。** 这个 Zipformer 是 zh-en 双语模型，不认泰语。
> 需要泰语字幕就把 ASR 引擎保持在 Whisper。

没装就在界面上选了 Sherpa 的话，点「开始」会失败，错误信息里会直接写明缺的是
`sherpa_onnx` 包还是模型文件。
