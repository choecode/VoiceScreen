# VoiceScreen 测试报告

测试环境：Windows 11 x64，Release / win-x64。最近验证日期：2026-08-03。

## 自动验证结果

| 项目 | 结果 |
|---|---|
| Release 全解决方案编译 | 通过，0 警告、0 错误 |
| xUnit 单元测试 | 5/5 通过 |
| WPF 主程序启动与释放 | 通过 |
| Discord 根进程及 Electron 进程树捕获 | 通过 |
| HyperX 麦克风与耳机枚举 | 通过 |
| VB-CABLE 播放端枚举和写入 | 通过 |
| 本地英文识别与英译中直连 | 通过 |
| 本地 VAD 分句后英译中 | 通过 |
| 中文识别、中译英和离线英文 TTS | 通过 |
| 中文测试文本中译英 | 通过 |
| 英文同时发送至 VB-CABLE 并在实体耳机试听 | 通过 |
| 发送结束后恢复原声麦克风 | 通过 |

本地接收测试结果：

```text
Enemies are on the second floor. Let's move to the left.
→ 敌人在二楼。让我们向左移动。
```

VAD 分句测试结果：

```text
Enemies are on the second floor.
→ 敌人在二楼。
```

## 网络边界

应用源代码中的模型调用地址只有：

- `http://127.0.0.1:18765`：应用自动启动的本地 faster-whisper + OPUS-MT 服务。

已删除讯飞 WebSocket、在线 TTS、云端测试工具以及界面中的 APPID、APIKey、Secret 配置。模型首次下载安装仍需联网，运行时不依赖外部 API。

专业翻译模型回归结果：

```text
我的英语很差啊，请不要介意啊。
→ My English is bad. Please don't mind.

敌人可能在三楼右边，先别冲。
→ The enemy could be on the third floor on the right. Don't attack for now.
```

检测到 Discord 中文时直接显示识别原文，OPUS-MT 调用被跳过。

## 仍需用户在真实通话中确认

自动测试无法代替真实 Discord 语音频道中的主观听感。请重点确认：

1. 对方连续说英语时，悬浮窗的英文原文和中文译文是否完整、延迟是否可接受。
2. 按住右 Alt 说中文、松开后，对方能否听见完整英文，特别是句尾单词。
3. 普通说话时，对方能否听见中文原声，且没有回声。
4. “翻译并试听（仅耳机）”是否只在耳机播放，不会进入 Discord。
5. Discord 的自动增益、回声消除和噪声抑制是否裁掉合成语音开头。
6. 游戏全屏时悬浮窗的位置、字号和换行是否合适。

若出现回声，首先确认 Discord 扬声器是实体耳机而不是 `CABLE Input`；Discord 麦克风必须是 `CABLE Output`。
