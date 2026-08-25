# VoiceScreen

VoiceScreen 是一款面向 Windows 11 的实时双向语音翻译工具。它可以只监听指定桌面应用的声音，显示实时原文和中文译文；也可以把麦克风中的中文转换成英文语音，通过 VB-CABLE 发送给 Discord 或其他语音应用。

当前推荐架构是 **Windows 客户端 + 局域网 NVIDIA DGX Spark**：

- Spark 上的 Qwen3-ASR 负责流式语音识别；
- Qwen3-4B 负责中英翻译和自然断句；
- Qwen3-TTS 使用私人参考录音合成英文音色；
- Windows 客户端负责进程音频捕获、Silero VAD、字幕、麦克风路由和播放；
- 本机 Whisper、Sherpa-ONNX、OPUS-MT、Windows Speech 仍作为可选回退链路。

当前生产配置和实测状态更新于 **2026-08-25**。

> 新机器安装、Spark 部署、VB-CABLE 设置和故障排查见 [部署指南](DEPLOYMENT.md)。
>
> 组件职责、数据流、状态机和设计取舍见 [项目说明](PROJECT_GUIDE.md)。
>
> 自动测试与延迟数据见 [测试报告](TEST_REPORT.md)。
> 模型来源与许可证见 [第三方模型说明](THIRD_PARTY_MODELS.md) 和 [第三方声明](THIRD_PARTY_NOTICES.md)。

## 已实现能力

- 监听用户选择的任意桌面应用及其子进程树，例如 Discord、Chrome、播放器或游戏；不会静默退化成全系统录音。
- 对所选应用的英文语音做流式识别和中文翻译；检测到中文时直接显示原文。
- 实时内容只更新当前临时区域，确认后的句子才进入历史，避免重复翻译不断堆积。
- 由 Silero VAD 判断语音活动；模型不可用时自动回退到 RMS 音量门限。
- 由 Qwen3-4B 辅助判断自然语义边界，避免仅按固定秒数截断长句。
- 悬浮窗固定使用 14 号字幕，自动跟随最新内容；用户手动滚动后保留历史浏览位置，回到底部后恢复自动跟随。
- 平时把真实麦克风原声转发给语音应用；按住右 Alt 说中文，松开后发送英文翻译语音。
- 可选“按自然短句提前发送”，按键未松开时即可逐句识别、翻译和播放，降低长段中文的首句等待时间。
- 私人克隆音色的长英文会按自然边界切块，第一块生成后立即播放，后续块与播放并行生成。
- 找不到 VB-CABLE 或发送设备时，接收字幕仍可继续以“仅字幕模式”运行。
- 仍可选择 Windows 已安装的英文音色、MyMemory 翻译或 Edge TTS 作为备用。

## 当前架构

```text
所选应用进程树
  → Windows Process Loopback
  → 16 kHz PCM
  → Silero VAD
  → Spark Qwen3-ASR-1.7B 流式会话
  → 稳定前缀 + Qwen3-4B 语义边界
  → Qwen3-4B 英译中
  → 临时字幕 / 已确认历史

实体麦克风
  ├─ 普通状态 → VB-CABLE → 语音应用（中文原声）
  └─ 按住右 Alt → 中文 ASR → Qwen3-4B 中译英
                         → Qwen3-TTS 私人音色分块生成
                         → VB-CABLE + 可选本地耳机
```

| 能力 | 推荐实现 | 备用实现 |
|---|---|---|
| 实时 ASR | Spark / Qwen3-ASR-1.7B | 本机 Whisper base+small 或 Sherpa-ONNX |
| 翻译与语义断句 | Spark / Qwen3-4B-Instruct-2507 | 本机 OPUS-MT 或 MyMemory |
| 英文 TTS | Spark / Qwen3-TTS-12Hz-0.6B-Base 私人音色 | Windows Speech 或 Edge TTS |
| 语音活动检测 | 客户端 Silero VAD ONNX | RMS 门限自动回退 |
| 应用独立捕获 | Windows Process Loopback | 无全系统捕获回退 |
| 发送音频路由 | VB-Audio Virtual Cable | 关闭发送功能，仅显示字幕 |

Spark 生产服务：

| 端口 | 服务 | 主要接口 |
|---:|---|---|
| `18765` | Qwen ASR、翻译、语义断句 | `/health`、`/transcribe`、`/translate`、`/segment` |
| `18766` | Qwen 私人音色 TTS | `/health`、`/synthesize` |
| `18767` | CosyVoice A/B 实验服务 | 只在 `experiments` profile 中手动启动 |

除健康检查外，模型接口都要求 `X-VoiceScreen-Token`。生产端口只应开放给可信局域网，不应直接暴露到公网。

## 最短使用流程

1. 先打开要监听的应用，例如 Discord、Chrome 或播放器。
2. 启动完整发布目录中的 `VoiceScreen.App.exe`，不要只复制一个 EXE。
3. 在“监听应用”中选择目标进程；列表没有目标时点“刷新列表”。
4. 只需要字幕时，关闭“同时把麦克风中文翻译成英文并发送到语音应用”。
5. 需要发送英文时，安装 VB-CABLE，并在 Discord 中把输入设备设为 `CABLE Output`。
6. 在高级设置中确认 Spark 地址为 `http://spark-host.local:18765/`，填入访问令牌。
7. 选择 `Spark Qwen3-ASR 1.7B`、`Spark Qwen3-4B`，并在本地英文音色中选择私人音色。
8. 点击“开始监听”。顶部出现“运行中”才代表启动完成。
9. 正常说话会发送中文原声；按住右 Alt 说中文、松开后发送英文。

应用当前固定使用 14 号字幕。悬浮窗支持鼠标滚轮和 `PgUp` / `PgDn` 浏览历史；手动离开底部时不会被新字幕强行拉回。

## 本机回退模式

没有 Spark 时，可以准备本机 Whisper、OPUS-MT 和可选 Sherpa-ONNX：

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\setup_local_models.ps1

# 可选：同时安装中英双语 Sherpa-ONNX Zipformer
powershell -ExecutionPolicy Bypass -File .\tools\setup_local_models.ps1 -Sherpa
```

然后在高级设置中把 ASR 改为“本地 Whisper”或“本地 Sherpa-ONNX Zipformer”，TTS 音色改为已安装的 Windows 英文音色。本机回退适合离线应急，但当前质量和低延迟目标以 Spark 路线为准。

## 实测性能摘要

以下数据来自 28 号 DGX Spark 预热后的同一组中文文本，包含翻译和第一段英文音色生成，不包含最终音频播放时长：

| 中文长度 | Qwen 分块首段可播放 | CosyVoice 流式首音频 |
|---:|---:|---:|
| 31 字 | 约 5.6 秒 | 约 6.5 秒 |
| 67 字 | 约 4.3 秒 | 约 7.0 秒 |
| 124 字 | 约 8.0 秒 | 约 9.0 秒 |

124 字中文的翻译阶段约 4.3 秒。旧的“整段英文全部合成完再播放”路径约需 22.8 秒；当前私人音色会自然分块并边生成边播放，因此不再等待完整长句音频。

这不是网络或所有语句的固定 SLA。停顿位置、翻译后的英文长度、Spark 当前负载、局域网抖动和 Discord 音频处理都会影响实际延迟。详细方法和数据见 [测试报告](TEST_REPORT.md)。

## 开发、测试与发布

要求 .NET 8 SDK；本机回退服务还需要 Python 3.11。

```powershell
# 全部 C# 与 Python 测试
powershell -ExecutionPolicy Bypass -File .\tools\run_tests.ps1

# 编码检查
powershell -ExecutionPolicy Bypass -File .\tools\check_encoding.ps1

# 自包含 win-x64 发布
dotnet restore src\VoiceScreen.App\VoiceScreen.App.csproj -r win-x64
dotnet publish src\VoiceScreen.App\VoiceScreen.App.csproj `
  -c Release -r win-x64 --self-contained true --no-restore `
  -o dist\VoiceScreen-release
```

当前自动验证基线：

- xUnit：`134/134`；
- Python 合约测试：`52/52`；
- Release 构建：0 警告、0 错误；
- Spark `18765` 与 `18766`：健康；
- Windows 发布包：真实启动并进入“运行中”，日志确认 Silero VAD 已加载。

## 目录结构

| 路径 | 职责 |
|---|---|
| `src/VoiceScreen.App` | WPF 客户端、音频捕获与路由、字幕 UI、服务客户端 |
| `src/VoiceScreen.Core` | 分句、稳定前缀、语种、去重、病态输出检测等可测试纯逻辑 |
| `src/VoiceScreen.App/LocalService` | 本机 Whisper/Sherpa/OPUS 回退服务与浏览器评测台 |
| `deploy/spark` | Qwen 生产服务、私人音色 TTS 和 CosyVoice 实验 profile |
| `tests` | C# 单元测试与 Python 服务契约测试 |
| `tools` | 模型准备、自检、ASR/VAD/Spark 延迟基准工具 |

## 隐私与已知限制

- 默认不保存原始录音，字幕历史只保存在当前进程内存中。
- Spark 模式会把捕获的语音和待翻译文本发送到配置的局域网模型服务；不会发送到公共云，除非主动选择 MyMemory 或 Edge TTS。
- 私人参考音频和生成的声纹 prompt 只保存在 Spark 的 `voice-profiles` 目录，不应提交到 Git。
- Process Loopback 得到的是目标应用的混合输出，无法可靠显示 Discord 中的真实说话人用户名。
- 多人重叠说话、强背景音乐、极短语气词和不自然停顿仍可能降低识别与断句质量。
- CosyVoice 已完成 A/B，但在当前 Spark 上没有胜过 Qwen 分块方案，因此不是生产默认服务。
- 程序不注入游戏、不读取游戏内存，也不绕过反作弊或 Windows 权限边界。
