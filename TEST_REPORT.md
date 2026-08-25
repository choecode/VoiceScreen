# VoiceScreen 测试报告

当前基线日期：**2026-08-25**。本报告对应 `main` 分支提交 `aec620e` 及其之前的完整功能链。

## 1. 测试环境

### Windows 客户端

- Windows 11 x64；
- .NET SDK 8.0.424；
- Release / win-x64 自包含发布；
- Windows Process Loopback；
- VB-Audio Virtual Cable；
- Silero VAD v6.2 ONNX / ONNX Runtime 1.29.0。

### 模型服务器

- NVIDIA DGX Spark，ARM64，128 GB 统一内存；
- `Qwen3-ASR-1.7B`；
- `Qwen3-4B-Instruct-2507`；
- `Qwen3-TTS-12Hz-0.6B-Base`；
- 生产端口 `18765`、`18766`；
- CosyVoice 实验端口 `18767`，测试后已停止。

## 2. 自动化验证

统一入口：

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\run_tests.ps1
```

当前结果：

| 项目 | 结果 |
|---|---|
| Release 解决方案构建 | 通过，0 警告、0 错误 |
| xUnit | `134/134` 通过 |
| Python unittest | `52/52` 通过 |
| 总自动测试 | `186/186` 通过 |
| Python 基准脚本语法 | `py_compile` 通过 |
| Git 补丁空白检查 | `git diff --check` 通过 |

主要覆盖范围：

- 中英泰语种判断和翻译方向契约；
- Whisper/Sherpa/Qwen ASR 选项和流式会话契约；
- Qwen 语义断句接口只接受 `BREAK` / `CONTINUE`；
- 稳定前缀、词边界回退、滚动窗口和最终定稿；
- 周期短语、重复字符和异常膨胀译文过滤；
- 中文短句抢跑后的已发送前缀对齐；
- 英文 TTS 自然分块、最大长度和悬空连接词规避；
- PCM 电平、音频设备分类和回退逻辑；
- 翻译与 TTS Provider 独立组合；
- 本机 Python 服务健康、评测台、安全响应头和路径白名单；
- 本地子进程生命周期与接口兼容性。

## 3. Spark 生产服务验证

健康检查实测：

```json
{
  "asr": "qwen3-asr-1.7b",
  "asrDevice": "spark-gpu",
  "asrStreaming": true,
  "asrStreamingChunkMs": 1000,
  "ready": true,
  "segmentation": "qwen3-4b-instruct-2507",
  "translation": "qwen3-4b-instruct-2507"
}
```

```json
{
  "device": "spark-gpu",
  "ready": true,
  "sampleRate": 24000,
  "tts": "qwen3-tts-12hz-0.6b-base",
  "voiceId": "my-voice"
}
```

Docker 状态：

- `voicescreen-model-service`：healthy；
- `voicescreen-tts-service`：healthy；
- `voicescreen-cosyvoice-service`：完成 A/B 后停止，不占用生产 GPU 资源。

## 4. Windows 真实启动冒烟测试

发布目录：`dist/VoiceScreen-release-final`。

已验证：

- `VoiceScreen.App.exe` 成功启动且窗口响应；
- 用户选择 Discord 进程并点击“开始监听”；
- 顶部状态进入“运行中”；
- Spark 模型服务连接成功；
- 音频路由以 WASAPI shared mode 建立；
- Process Loopback 成功绑定选定 Discord 根进程；
- 日志明确记录 `Silero VAD enabled for selected-process audio (16kHz ONNX)`；
- Discord 当时没有播放声音，状态栏正确显示“5 秒内未检测到声音”，而不是把静音误判为启动失败；
- 应用持续响应，无启动异常或服务重连循环。

这次冒烟测试证明启动、连接、路由和 VAD 装载链路可用；它不等同于一次真实远端 Discord 用户的主观通话验收。

## 5. Silero VAD 对比

基准脚本：

```powershell
python .\tools\benchmark_silero_vad.py
```

输入包括用户的 Spark 私人音色参考语音，以及相近响度的纯音、白噪声和静音。结果：

| 信号 | Silero 判定为语音 | 旧 RMS 判定为语音 | Silero 概率中位数 |
|---|---:|---:|---:|
| 参考语音 | 86.7% | 74.9% | 0.999 |
| 纯音 | 0% | 100% | 接近 0 |
| 白噪声 | 0% | 100% | 接近 0 |
| 静音 | 0% | 0% | 接近 0 |

CPU ONNX 推理耗时：

- 中位数约 `0.076 ms / 512 samples`；
- P95 约 `0.079 ms / 512 samples`。

结论：Silero 能显著减少音乐和稳定噪声导致的假语音段，计算成本相对 32 ms 音频窗口可以忽略。RMS 保留为模型缺失或运行异常时的兼容回退，而不是主判据。

## 6. Qwen 与 CosyVoice TTS A/B

基准脚本：

```bash
VOICESCREEN_API_TOKEN='***' \
python tools/benchmark_spark_pipeline.py --host http://127.0.0.1
```

测试使用三段固定中文，先由同一 Qwen3-4B 翻译为英文，再比较 Qwen 私人音色、CosyVoice 完整生成和 CosyVoice 真流式输出。

### 6.1 未分块的原始模型结果

| 中文 | 翻译 | Qwen 整句生成 | Qwen 音频时长 | Qwen RTF | Cosy 流式首音频 | Cosy 总生成 | Cosy RTF |
|---:|---:|---:|---:|---:|---:|---:|---:|
| 31 字 | 1.72 s | 5.77 s | 8.40 s | 0.686 | 4.74 s | 6.01 s | 0.699 |
| 67 字 | 2.23 s | 9.62 s | 14.96 s | 0.643 | 4.74 s | 12.47 s | 0.690 |
| 124 字 | 4.28 s | 18.57 s | 28.88 s | 0.643 | 4.72 s | 22.68 s | 0.688 |

如果客户端等待 Qwen 整句 WAV，124 字中文从开始翻译到音频可用约需 `22.85 s`，不可接受。CosyVoice 真流式将该场景的首音频降到约 `8.99 s`，但总生成速度没有胜过 Qwen。

### 6.2 客户端自然分块后的生产结果

当前 `SpeechChunker` 把英文按约 60、最多 80 字符切块，第一块生成后立即送入播放队列：

| 中文 | 翻译后英文 | 分块数 | 第一块长度 | 翻译开始到第一块可播放 |
|---:|---:|---:|---:|---:|
| 31 字 | 97 字符 | 2 | 76 字符 | 约 5.62 s |
| 67 字 | 219 字符 | 4 | 41 字符 | 约 4.33 s |
| 124 字 | 446 字符 | 8 | 79 字符 | 约 8.03 s |

与同一组三种长度的 CosyVoice 端到端首音频约 `6.47 s / 6.98 s / 8.99 s` 相比，Qwen 自然分块在当前 Spark 上三组都更快，而且依赖更少、总 RTF 更好。

结论：

- 生产继续使用 Qwen3-TTS + 客户端自然分块；
- CosyVoice 保留为可复现实验，不默认启动；
- 分块必须避免停在 `and`、`because`、`whether`、`don't` 等连接词，相关回归测试已经加入 xUnit。

## 7. 延迟如何解读

“实时翻译延迟”不是单一数字，应分开记录：

1. 捕获到 VAD 确认语音；
2. ASR 临时文本出现；
3. ASR 最终文本确认；
4. 翻译完成；
5. 第一段 TTS 入队；
6. 最后一段 TTS 播放完成。

本报告表格中的“第一块可播放”是第 4 与第 5 阶段的组合，不包含用户说完整段话所花时间，也不包含整段英文最终播放完毕的时长。不同说话停顿、翻译后长度和局域网负载会改变结果。

## 8. 发布包检查

自包含 win-x64 发布已确认包含：

- VoiceScreen 客户端及 .NET 运行时；
- `VoiceScreen.Core.dll`；
- ONNX Runtime 原生库；
- `Models/silero_vad_16k_op15.onnx`；
- `THIRD_PARTY_NOTICES.md`；
- 本机回退 Python 服务文件和浏览器评测台资源。

旧发布目录未被覆盖，新的真实运行验证使用独立发布目录完成。

## 9. 尚需持续做的人工验收

自动测试和模型基准不能代替真实通话。每次发布仍应确认：

1. Discord、Chrome 和播放器分别作为目标时，能否捕获正确进程而不混入其他系统声音。
2. 对方连续英语、多人轮流说话和背景音乐场景下，字幕边界是否自然且无重复。
3. 用户手动浏览历史时，新字幕是否继续写入但不抢走滚动位置。
4. 按住右 Alt 说长中文时，对方是否能听见完整英文开头、分块连接和最后一个单词。
5. Discord Krisp、自动增益和输入灵敏度是否裁掉 TTS 开头。
6. Spark 重启、TTS 临时失败和 VB-CABLE 缺失时，回退提示是否清楚。
7. 私人音色是否经本人授权；更换录音后 prompt 是否正确重建。

## 10. 复现命令

```powershell
# C# + Python
powershell -ExecutionPolicy Bypass -File .\tools\run_tests.ps1

# 仅 C#
powershell -ExecutionPolicy Bypass -File .\tools\run_tests.ps1 -DotnetOnly

# 仅 Python
powershell -ExecutionPolicy Bypass -File .\tools\run_tests.ps1 -PythonOnly

# 源码编码
powershell -ExecutionPolicy Bypass -File .\tools\check_encoding.ps1
```

```bash
# Spark 健康
curl -fsS http://127.0.0.1:18765/health
curl -fsS http://127.0.0.1:18766/health

# 容器状态
docker ps --filter name=voicescreen
```
