# VoiceScreen 项目说明

本文描述 2026-08-25 的当前实现。历史上 VoiceScreen 曾以“Windows 本机 Whisper + OPUS-MT”为唯一架构；现在它仍是回退方案，生产默认已经升级为 **Windows 客户端 + DGX Spark/Qwen**。

## 1. 产品定位

VoiceScreen 解决两个相互独立的问题：

1. **听懂对方**：只捕获用户选中的桌面应用，在悬浮窗显示实时原文和中文翻译。
2. **让对方听懂自己**：平时发送真实麦克风原声；按住右 Alt 说中文时，改为发送英文翻译语音。

发送链路可以关闭。即使没有 VB-CABLE、麦克风或英文 TTS，用户仍可使用完整的实时字幕功能。

## 2. 设计原则

- **捕获范围明确**：只捕获选定根进程及其子进程，不在失败时偷偷改成整机声音。
- **临时结果与历史分离**：实时尾部可以修改，定稿历史不能反复覆盖或重复追加。
- **音频线程不等待模型**：捕获、ASR、翻译、UI、TTS 各自排队，慢请求不能阻塞音频采集。
- **长句先播后算**：私人音色按自然语义分块，第一块可播放后立即入队。
- **发送失败不拖垮字幕**：VB-CABLE 或麦克风异常只关闭发送功能。
- **生产与实验隔离**：CosyVoice 只属于 Compose `experiments` profile，不会随生产服务启动。
- **模型服务有边界**：局域网服务必须使用令牌，端口不能直接暴露公网。

## 3. 接收链路：应用语音到中文字幕

```text
选定根进程及其子进程
  → Windows Process Loopback Capture
  → 自动重建捕获会话（进程或音频会话变化时）
  → PCM 16 kHz / mono / s16le
  → Silero VAD 流式窗口
  → 预滚缓冲 + 当前语句缓冲
  → Qwen3-ASR 流式会话
  → 临时转写 / 稳定前缀
  → 语义边界策略 + Qwen3-4B BREAK/CONTINUE
  → 增量翻译
  → 临时字幕原地更新
  → 最终定稿进入历史
```

### 3.1 进程音频捕获

用户可以选择 Discord、Chrome、播放器或其他有窗口/音频能力的桌面进程。`ProcessTargetService` 保存进程名、可执行路径和本次 PID；应用重启导致 PID 改变后，会按名称和路径重新定位。

`ResilientProcessLoopbackCapture` 捕获完整进程树，并监控目标进程与音频会话变化。捕获初始化失败会给出明确错误，不会降级为系统混音，因此不会意外把游戏、浏览器或 TTS 输出送去识别。

### 3.2 Silero VAD

客户端内置 `silero_vad_16k_op15.onnx`：

- 输入为 16 kHz 单声道 PCM；
- 每 512 个新采样执行一次推理，并携带 64 个上下文采样与循环状态；
- 使用 `0.50` 开启、`0.35` 关闭的滞回阈值，减少边界抖动；
- ONNX Runtime CPU 推理，不占 Spark GPU；
- 模型缺失或推理异常时 fail-open 回退到原有 RMS 门限，监听会话不会因此终止。

这一步解决了“音乐或稳定噪声响度很高，被 RMS 当成人声”的主要问题。

### 3.3 流式 ASR 与最终定稿

推荐的 Qwen3-ASR 服务以会话 ID 保存当前语句状态：

- 客户端只发送上次之后新增的 PCM；
- 服务每累计约 1 秒音频更新一次流式预览；
- 未稳定尾部保留回滚空间，避免每次结果都从头覆盖；
- 单会话最多 60 秒，空闲 120 秒清理，最多 32 个会话；
- 语句结束时再用完整上下文执行一次最终识别，然后释放服务端会话。

本机 Whisper 回退采用滚动窗口：base 提供预览、small 负责定稿；Sherpa-ONNX 回退则维持真正的流式解码状态。

### 3.4 自然断句

VoiceScreen 不再只用“固定秒数”决定字幕边界。当前策略组合以下信号：

- VAD 静音长度；
- 临时文本是否稳定；
- 标点、长度和最大语句时长；
- Qwen3-4B `/segment` 返回的受限 `BREAK` / `CONTINUE` 判断。

模型只有明确回复 `BREAK` 才会接受边界；任何解释性或异常输出都按 `CONTINUE` 处理。这样能避免上一条字幕停在冠词、介词、连词或未完成从句上。

### 3.5 增量翻译与去重

ASR 和翻译分别使用工作队列：

- 临时版本只保留最新快照；
- 已确认的最终语句不会被临时任务覆盖；
- 新译文只替换实时预览，不进入永久历史；
- 最终结果经过周期短语、重复膨胀和源译长度异常检测；
- 中文字符分布优先于 ASR 的语言标签，减少短句误标；
- 检测到中文时直接显示，不再做中文到中文翻译。

## 4. 发送链路：中文到英文语音

### 4.1 普通状态

```text
实体麦克风 → VoiceScreen WASAPI Router → CABLE Input → CABLE Output → 语音应用
```

VoiceScreen 必须始终参与麦克风直通；Discord 的输入设备因此固定选择 `CABLE Output`。

### 4.2 按住右 Alt

```text
按下右 Alt
  → 暂停中文原声直通
  → 录制实体麦克风
  → 可选：自然短句抢跑

松开右 Alt
  → Qwen3-ASR 中文定稿
  → Qwen3-4B 中译英
  → Qwen3-TTS 私人音色 / Windows Speech / Edge TTS
  → VB-CABLE
  → 可选同步到本地耳机
  → 排空队列和尾部静音
  → 恢复原声直通
```

全局热键同时使用 Raw Input、低级键盘钩子和异步按键状态轮询，并对三路事件去重。程序不向目标应用注入代码。

### 4.3 自然短句抢跑

“中文讲话时按自然短句提前发送”默认关闭。开启后，`OutgoingClauseStreamer` 会在按键尚未松开时观察稳定中文前缀，在自然边界处提前识别、翻译并将 TTS 放入播放队列。

松手后的最终识别会用内容对齐找出尚未发送的剩余部分，而不是按字符数硬切，因此不会把已经播放的开头重复发送。风险是已播出的英文无法撤回，所以用户需要主动开启。

### 4.4 私人音色长句分块

Qwen3-TTS 对长文本整段生成时，等待时间会随英文长度增长。`SpeechChunker` 采用以下顺序找切点：

1. 句号、问号、感叹号；
2. 逗号、分号、冒号等短语边界；
3. 安全的单词空格；
4. 极端情况下才按最大长度硬切。

默认优先约 60 字符、最多 80 字符。切点会避开 `and`、`because`、`whether`、`don't`、`to`、`of` 等悬空连接词。第一块合成后立即播放，生成第二块时第一块已经在 VB-CABLE 中播放。

### 4.5 半双工与防循环

核心状态为：

```text
Listening
  → CapturingChinese
  → Translating
  → SpeakingEnglish
  → Cooldown
  → Listening
```

播放合成英文期间，麦克风直通和接收 ASR 都会暂停；播放、耳机监听和尾部静音排空后才恢复。发送与接收使用不同的音频端点，再加上状态门控和最近发送文本记忆，避免程序识别自己的 TTS 并无限翻译。

## 5. 模型与 Provider 组合

### 5.1 ASR

| 界面选项 | 行为 | 适用情况 |
|---|---|---|
| Spark Qwen3-ASR 1.7B | 连接配置的 `18765`，使用流式会话 | 当前推荐生产方案 |
| 本地 Whisper | 自动启动本机 Python 服务，base 预览、small 定稿 | 无 Spark、需要中英泰离线回退 |
| 本地 Sherpa-ONNX Zipformer | 本机流式中英双语识别 | 低资源回退，不支持泰语 |

### 5.2 翻译

| 界面选项 | 行为 |
|---|---|
| Spark Qwen3-4B | Qwen ASR 模式下连接局域网服务；本机 ASR 模式下由本机 OPUS-MT 备用服务处理 |
| 云端 MyMemory | 只发送识别后的文本，不上传原始音频 |

### 5.3 TTS 与音色

“语音合成”与“本地英文音色”共同决定实际路径：

- 语音合成为本地，音色选择私人音色：连接同一 Spark 主机的 `18766`；
- 语音合成为本地，选择 Windows 音色：调用 Windows Speech / SAPI；
- 语音合成为云端：调用 Edge TTS，并使用单独的 Edge 音色选项。

Spark 私人音色不可用时会记录警告，并回退到 Windows 英文音色；不会让整条发送链路失效。

ASR 后端和服务地址需要停止会话后修改并重新开始。翻译/TTS Provider 的切换以新语句为边界；为避免同一句前后使用不同模型，正在处理的语句会冻结开始时的选择。

## 6. Windows 客户端界面

### 6.1 主区

- **监听应用**：当前要捕获的进程；显示应用名、PID 和窗口标题。
- **刷新列表**：目标应用启动或重启后重新枚举。
- **同时把麦克风中文翻译成英文并发送**：关闭后不要求 VB-CABLE，应用以仅字幕模式运行。
- **开始监听 / 停止**：建立或释放模型、音频和热键会话。
- **调整字幕窗**：解锁位置和尺寸；锁定后恢复鼠标穿透。

### 6.2 高级设置

- ASR、推理设备、翻译、TTS；
- 低延迟实时字幕；
- Spark 服务地址与加密保存的访问令牌；
- 实体麦克风、本地监听输出、VB-CABLE、音色；
- 自然短句提前发送；
- 在线服务测试与中文翻译测试；
- 因权限无法捕获目标时，以管理员身份重启。

### 6.3 悬浮字幕

| 前缀 | 含义 |
|---|---|
| `EN:` | 对方英文原文 |
| `TH:` | 对方泰语原文 |
| `中:` | 中文译文或中文原文 |
| `我说:` | 麦克风识别到的中文 |
| `已发送:` | 实际进入 TTS 的英文 |
| 实时区域 | 尚可修改的当前识别和译文 |

字幕固定为 14 号。历史上限默认 200 条，退出后不保存。正常位于底部时自动跟随；用户滚轮或使用 `PgUp` 浏览旧内容后暂停自动跟随，回到底部或按 `PgDn` 到最新位置后恢复。

## 7. Spark 服务

### 7.1 生产服务

| 容器 | 端口 | 模型 | 作用 |
|---|---:|---|---|
| `voicescreen-model-service` | `18765` | Qwen3-ASR-1.7B + Qwen3-4B-Instruct-2507 | ASR、翻译、语义断句 |
| `voicescreen-tts-service` | `18766` | Qwen3-TTS-12Hz-0.6B-Base | 私人英文音色 |

两者使用 NVIDIA GPU、主机网络和本地只读模型卷。健康检查无需令牌；业务 POST 接口必须提供 `X-VoiceScreen-Token`。

### 7.2 CosyVoice 实验服务

`voicescreen-cosyvoice-service` 位于 `experiments` profile，端口 `18767`。它实现完整 WAV 和真实流式 PCM 两个接口，只用于可复现 A/B。

实测中 CosyVoice 的首音频比 Qwen 整句方案快，但没有胜过客户端完成自然分块后的 Qwen 路线；总实时率也略低，因此当前不进入生产启动集合。

## 8. 异常处理与背压

- 音频捕获线程只写入缓冲，不直接等待 HTTP。
- ASR 临时任务积压时丢弃过期快照，只保留最新预览。
- 翻译队列对最终结果和临时结果分别处理，最终结果优先。
- 单次中文识别与翻译最长 35 秒；TTS 与完整播放最长 60 秒。
- 已播放的 TTS 块无法撤回；后续块失败时先排空已入队音频，再恢复中文原声。
- Spark 私人音色失败可回退 Windows Speech；Silero 失败可回退 RMS；发送设备失败可回退仅字幕。
- 模型服务、子进程和监听对象在停止时显式释放；本机 Python 服务加入 Windows Job Object，主程序异常退出时由系统回收。

## 9. 项目结构

```text
VoiceScreen/
├─ src/
│  ├─ VoiceScreen.App/                 WPF、音频、模型客户端、Silero ONNX
│  └─ VoiceScreen.Core/                可测试的纯逻辑
├─ deploy/spark/                       Spark Docker 服务
├─ tests/
│  ├─ VoiceScreen.Tests/               xUnit
│  └─ python/                          Python 服务契约
├─ tools/
│  ├─ VoiceScreen.SelfTest/            真实设备和模型自检
│  ├─ benchmark_asr.py                 ASR 噪声基准
│  ├─ benchmark_silero_vad.py          VAD 信号基准
│  ├─ benchmark_spark_pipeline.py      Qwen/CosyVoice 延迟基准
│  └─ setup_local_models.ps1           Windows 本机回退模型准备
├─ DEPLOYMENT.md
├─ TEST_REPORT.md
├─ THIRD_PARTY_MODELS.md
└─ THIRD_PARTY_NOTICES.md
```

`VoiceScreen.Core` 主要包含：

| 类型 | 职责 |
|---|---|
| `IncrementalTranscript` | 稳定前缀提取和词边界回退 |
| `TranscriptWindow` | 滚动窗口时间戳裁剪 |
| `TranscriptSanitizer` | 周期重复、异常膨胀和幻觉过滤 |
| `SubtitleBoundaryPolicy` | 本地边界信号组合 |
| `ClauseSegmenter` | 中文抢跑后的内容对齐与剩余部分计算 |
| `SpeechChunker` | 英文 TTS 自然分块与悬空词规避 |
| `SpokenLanguage` | 基于字符分布的中英泰语种判断 |
| `TranslationDirection` | 用户翻译方向与实际模型对映射 |
| `AudioDeviceClassifier` | 实体设备和虚拟音频线分类 |

## 10. 数据与安全边界

- 默认不把录音或字幕写入磁盘；日志只记录状态、长度、延迟和错误，不应记录访问令牌。
- Windows 设置保存在 `%LOCALAPPDATA%\VoiceScreen\settings.dat`，访问令牌通过当前用户的 Windows 数据保护加密。
- Spark 模式会将 PCM 和文本发送到用户配置的局域网地址。
- MyMemory 与 Edge TTS 是显式选择的在线备用；启用后文本会离开本地网络。
- 私人参考音频与 `my-voice.pt` 声纹缓存仅保存在 Spark 主机，不应进入源码仓库或发布包。
- 业务端口不提供 TLS；安全依赖可信局域网、主机防火墙和随机令牌。跨网段或公网使用时必须另加 VPN 或 TLS 反向代理。

## 11. 已知限制

- Process Loopback 无法从 Discord 混合音轨恢复真实说话人用户名。
- 重叠说话和强背景音乐仍会降低识别准确率。
- 语义边界模型能减少机械截断，但不能保证每个口语停顿都符合书面断句。
- 长中文首音频仍受翻译长度和首个 TTS 分块长度影响，当前不是亚秒级端到端同传。
- Qwen TTS 服务按请求串行生成，多个客户端同时使用会排队。
- Windows 安全中心可能对未签名发布包给出警告；正式对外分发需要代码签名。

部署、验证和故障排查见 [DEPLOYMENT.md](DEPLOYMENT.md)，当前实测数据见 [TEST_REPORT.md](TEST_REPORT.md)。
