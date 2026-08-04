# 免费自建服务模式

VoiceScreen 客户端可连接 `http://voice.choenas.top:88/` 上的自托管 OPUS-MT + Piper 服务，不使用科大讯飞，也不需要任何收费 API 或密钥。

当前服务的 `/health` 返回 `asr: disabled`，因此现阶段的链路是：

```text
Discord/麦克风音频 → 本机 faster-whisper 识别
识别文本 → voice.choenas.top:88/evaluate → OPUS-MT 翻译
我方中文 → 服务器 OPUS-MT → Piper 英语 WAV → 客户端 → VB-CABLE → Discord
```

也就是说，翻译和语音合成在线完成，但 ASR 仍在本机免费运行。若以后要让客户端完全不装模型，服务器需要新增接收 PCM/WAV 的 ASR 接口，并在 `/health` 中启用 ASR；仅靠现有 `/evaluate` 文本接口无法完成纯在线语音识别。

## 使用

1. 在运行模式中选择“免费自建服务”。
2. 服务地址保持 `http://voice.choenas.top:88/`。
3. 点击“测试自建服务与延迟”。
4. 测试成功后启动；Discord 和 VB-CABLE 设置与本地模式相同。

客户端使用 Web 评测台相同的协议：`GET /health`、`GET /providers`、`POST /evaluate` 和返回的 `/audio/*.wav`。固定选用服务器上的 `local-opus`，不会调用 Web 页面另一个 `mymemory-edge` 第三方提供商。

## 中国网络实测（2026-08-04）

测试出口为中国电信云南昆明：主页约 90 ms，热连接 `/health` 约 20–95 ms，一次短句中译英约 614 ms；本地 OPUS-MT + Piper 的短句翻译及完整语音流水线实测约 3.3 秒，其中翻译约 489 ms、Piper 合成约 2.8 秒。结果会受服务器负载、线路和句长影响。

当前地址是明文 HTTP 88。语音本身不会上传，但识别后的文本会上传到自建服务器；请勿发送敏感内容。后续应给域名配置 HTTPS。

## 战地全屏热键

右 Alt 同时由 Raw Input、低级键盘钩子、异步按键状态轮询和 `RegisterHotKey` 监听，并进行聚合去重。若战地以管理员权限运行，请点击主界面的“以管理员重启（战地）”，确保 VoiceScreen 与游戏拥有同等权限。程序不注入游戏、不读取游戏内存，也不绕过反作弊。

