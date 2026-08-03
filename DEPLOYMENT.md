# VoiceScreen 完整部署教程（Windows 11）

本文从一台尚未安装任何依赖的 Windows 11 x64 电脑开始，完成 VoiceScreen 的安装、模型准备、Discord 音频配置、首次验证、离线使用和故障排查。

## 1. 部署结果与工作方式

部署完成后：

- Discord 对方说英语或泰语时，VoiceScreen 只捕获 Discord 进程声音，在悬浮窗显示原文与中文译文。
- 你平时说话时，实体麦克风的中文原声会通过 VoiceScreen 转发给 Discord。
- 按住右 Alt 说中文、松开后，VoiceScreen 在本地生成英文字幕和英文语音，并通过虚拟麦克风发给 Discord。
- 实际发送的英文可以同步在实体耳机试听；中文测试框也能只在耳机试听翻译效果，不会把测试音发给 Discord。
- Whisper、三个 OPUS-MT 专用翻译模型和英文语音合成都在本机运行。泰语通过 th-en → en-zh 桥接翻译；模型首次下载需要联网，之后可以断网使用。

音频路由关系如下：

```text
对方 → Discord 扬声器流 → VoiceScreen → 英文识别/英译中 → 悬浮字幕

你的 HyperX 麦克风 → VoiceScreen → CABLE Input → CABLE Output → Discord 麦克风
                                  ↑
                         英文合成语音也从这里发送

Discord 扬声器 → HyperX 实体耳机
```

## 2. 部署前检查

### 2.1 系统要求

- Windows 11 x64；Windows 10 22H2 或更新版本理论上也可运行，但当前项目主要在 Windows 11 验证。
- Discord 桌面客户端。浏览器版 Discord 不在支持范围内。
- x64 CPU，建议至少 16 GB 内存。
- 建议预留 8 GB 以上磁盘空间，用于 Whisper、OPUS-MT、一次性模型转换依赖和缓存。
- 一副耳机和实体麦克风。使用扬声器外放会产生真实的声学回声。
- 安装依赖和模型时需要联网；正式翻译时不需要公网。

### 2.2 PowerShell

后续命令请在普通 PowerShell 中运行。只有安装 VB-CABLE 驱动时需要管理员权限。

先检查 Windows Package Manager：

```powershell
winget --version
```

如果没有 `winget`，可从 Microsoft Store 安装或更新“应用安装程序”，也可以使用后文给出的官方下载页面手动安装各项依赖。

## 3. 安装运行依赖

### 3.1 安装 .NET 8 Desktop Runtime

运行发布版只需要 Desktop Runtime：

```powershell
winget install --id Microsoft.DotNet.DesktopRuntime.8 -e --accept-package-agreements --accept-source-agreements
```

验证：

```powershell
dotnet --list-runtimes
```

输出中应包含类似：

```text
Microsoft.WindowsDesktop.App 8.0.x
```

如果要从源码编译，请改装 .NET 8 SDK；SDK 已包含运行时：

```powershell
winget install --id Microsoft.DotNet.SDK.8 -e --accept-package-agreements --accept-source-agreements
dotnet --list-sdks
```

### 3.2 安装 Python 3.11 x64

```powershell
winget install --id Python.Python.3.11 -e --accept-package-agreements --accept-source-agreements
```

关闭当前 PowerShell，重新打开一个窗口，再验证：

```powershell
python --version
where.exe python
```

必须能看到 Python 3.11，并且 `python` 指向真实安装目录。VoiceScreen 启动本地识别服务时调用的是 `python`，不是 `py`。

如果 `python` 打开 Microsoft Store，进入“设置 → 应用 → 高级应用设置 → 应用执行别名”，关闭冲突的 `python.exe` / `python3.exe` 商店别名，然后重新打开 PowerShell。

### 3.3 一键安装本地模型

在 VoiceScreen 源码根目录运行：

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\setup_local_models.ps1
```

如果使用发布目录，则脚本就在 EXE 旁边：

```powershell
powershell -ExecutionPolicy Bypass -File .\setup_local_models.ps1
```

脚本会自动完成：

1. 安装 faster-whisper、CTranslate2、Transformers、SentencePiece 和安全版本的 CPU PyTorch。
2. 下载 Whisper small。
3. 从 Helsinki-NLP 官方仓库下载 `opus-mt-zh-en` 与 `opus-mt-en-zh`。
4. 把中译英、英译中、泰译英三个翻译模型转换为 CTranslate2 CPU INT8。

模型目录：

```text
%LOCALAPPDATA%\VoiceScreen\Models\
```

脚本最后出现 `All VoiceScreen local models are ready.` 才算完成。运行时只使用 CTranslate2 INT8，不使用 PyTorch 推理，也不会占用游戏显卡。

OPUS-MT 是专用机器翻译模型，不是聊天模型。中译英模型采用 CC-BY-4.0，英译中和泰译英模型采用 Apache-2.0；分发程序时应保留模型来源与许可说明。

### 3.4 安装 Windows 英文语音

打开：

```text
设置 → 时间和语言 → 语音 → 管理语音 → 添加语音
```

安装“英语（美国）”或其他英文语音。若想切换男女声，请至少安装一个男声和一个女声；VoiceScreen 会把所有可用英文音色列在“英文音色”下拉框中。完成后重启 VoiceScreen。中文 Windows 如果没有英文语音，程序将拒绝启动正式链路，而不会误用中文音色。

### 3.5 安装 VB-Audio Virtual Cable

1. 从 VB-Audio 官方页面下载 VB-CABLE Driver ZIP。
2. 解压 ZIP，右键 `VBCABLE_Setup_x64.exe`，选择“以管理员身份运行”。
3. 点击 `Install Driver`。
4. 安装完成后重启 Windows；不要只注销。
5. 打开“设置 → 系统 → 声音 → 更多声音设置”，确认存在：
   - 播放设备：`CABLE Input (VB-Audio Virtual Cable)`
   - 录制设备：`CABLE Output (VB-Audio Virtual Cable)`

打开 `CABLE Output` 的属性，确认“侦听”选项卡中的“侦听此设备”没有勾选。勾选它会让你听到自己的延迟回放，容易误以为程序产生回声。

## 4. 获取 VoiceScreen

### 4.1 直接使用已发布目录

如果开发者已经把 `VoiceScreen-local-offline` 文件夹发给你，完整解压到固定目录，例如：

```text
C:\Apps\VoiceScreen\
```

目录内必须同时存在：

```text
VoiceScreen.App.exe
VoiceScreen.App.dll
VoiceScreen.Core.dll
LocalService\local_outgoing_service.py
其他 .dll 和 .json 文件
```

不能只复制 `VoiceScreen.App.exe`，否则程序无法启动。

### 4.2 从私有 GitHub 仓库编译

先安装 Git 和 GitHub CLI，并登录有仓库权限的 GitHub 账号：

```powershell
winget install --id Git.Git -e
winget install --id GitHub.cli -e
gh auth login
```

克隆、编译并发布：

```powershell
Set-Location "$env:USERPROFILE\Desktop"
gh repo clone choecode/VoiceScreen
Set-Location VoiceScreen
dotnet restore VoiceScreen.sln
dotnet build VoiceScreen.sln -c Release
dotnet test tests\VoiceScreen.Tests\VoiceScreen.Tests.csproj -c Release --no-build
dotnet publish src\VoiceScreen.App\VoiceScreen.App.csproj `
  -c Release -r win-x64 --self-contained false `
  -o dist\VoiceScreen-local-offline
```

发布结果位于：

```text
dist\VoiceScreen-local-offline\VoiceScreen.App.exe
```

仓库是私有的；没有权限的账号无法克隆。

## 5. Windows 与 Discord 配置

### 5.1 Windows 麦克风权限

进入：

```text
设置 → 隐私和安全性 → 麦克风
```

打开：

- 麦克风访问权限。
- 允许应用访问麦克风。
- 允许桌面应用访问麦克风。

### 5.2 Discord 配置

先启动 Discord 桌面客户端，再进入“用户设置 → 语音和视频”：

| Discord 项目 | 必须选择 |
|---|---|
| 输入设备/麦克风 | `CABLE Output (VB-Audio Virtual Cable)` |
| 输出设备/扬声器 | HyperX 实体耳机 |
| 输入模式 | 语音活动，或根据个人习惯使用 Discord 按键说话 |

关键规则：

- Discord 麦克风不能选 HyperX。否则对方只能听见未经 VoiceScreen 路由的原声，听不到合成英文。
- Discord 扬声器不能选 `CABLE Input`。否则 Discord 对方的声音会被送回 Discord 输入，形成回声。
- Windows 的“侦听此设备”必须关闭。
- 建议首次测试时暂时关闭 Discord 的 Krisp 噪声抑制、自动增益和高级语音处理。如果英文 TTS 完整，再逐项开启并复测。
- 用耳机测试，不要让扬声器声音重新进入实体麦克风。

点击 Discord 自带的麦克风测试，说话时应看到输入电平变化。

## 6. VoiceScreen 首次启动

1. 确认 Discord 桌面客户端已经启动并登录。
2. 确认 `setup_local_models.ps1` 已成功完成。
3. 双击 `VoiceScreen.App.exe`。
4. 在“实体麦克风”中选择 HyperX 麦克风。
5. 确认界面显示：
   - Discord 声音捕获：自动，仅捕获 Discord 进程。
   - 发送给 Discord：`CABLE Input`。
6. 在“英文试听耳机”中选择 HyperX 实体耳机；按需要勾选“复读已发送英文”，并在“英文音色”中选择男声或女声。
7. 先保留“模拟模式（不加载本地模型）”，点击“启动”；直接说中文，在 Discord 麦克风测试中确认原声路由正常。
8. 点击“停止”。
9. **取消勾选“模拟模式（不加载本地模型）”**。
10. 再次点击“启动”，进入正式纯本地模式。
11. 在中文测试框输入一句话，点击“翻译并试听（仅耳机）”。悬浮窗会显示测试中英文，你会在耳机听到英文，但该测试音不会进入 Discord。

正式模式第一次启动会加载 Whisper 与三个 OPUS-MT 模型，可能需要数秒到几十秒。状态显示“纯本地模式 · 只监听 Discord · 原声麦克风已直通”后才算启动完成。

如果 Windows Defender SmartScreen 阻止启动，确认文件来自本项目仓库后，点击“更多信息 → 仍要运行”。不要从第三方网盘或下载站获取修改版程序。

## 7. 正式验收步骤

建议找一名 Discord 好友配合，按顺序验证。

### 7.1 原声直通

不按右 Alt，直接说中文。对方应该能听见中文原声，而且你自己不应听到延迟回放。

### 7.2 中文转英文发送

1. 按住键盘右侧的 Alt。
2. 看到悬浮窗顶部状态提示“正在听你说中文……”后说一句完整中文；这只是临时状态，不会留在字幕历史中。
3. 说完再松开右 Alt。
4. 悬浮窗应依次显示：

   ```text
   我说：敌人在二楼
   已发送：Enemies are on the second floor.
   ```

5. 对方应听到完整英文，尤其是最后一个单词。
6. 英文播放结束后再次直接说中文，确认原声已自动恢复。

### 7.3 对方英语转中文字幕

让对方说英语。悬浮窗应显示类似：

```text
EN: Enemies are on the second floor.
中: 敌人在二楼。
```

同时播放一个游戏中文视频或让游戏角色说中文，悬浮窗不应显示游戏内容，因为接收链路固定只捕获 Discord 进程树。

如果 Discord 对方改说中文，Whisper 会自动检测语言，悬浮窗直接显示 `中：识别原文`，不会调用 OPUS-MT 再做一次中文到中文翻译。

如果检测为泰语，悬浮窗显示 `TH：泰文原文` 和 `中：中文译文`。翻译在本地依次经过泰译英与英译中模型。

### 7.4 防循环验证

发送一次英文 TTS 后观察悬浮窗。程序不应把自己刚发送或在耳机试听的英文重新识别、翻译和累加。英文播放期间麦克风直通和接收识别都会暂停，虚拟声卡及耳机缓冲排空后才恢复。接收端还固定只捕获 Discord 进程声音，不会捕获本机耳机输出。

## 8. 断网验证

只有在模型准备脚本成功完成后再执行：

1. 退出 VoiceScreen。
2. 暂时断开网络。
3. 重新启动 VoiceScreen，取消模拟模式并点击“启动”。
4. 重复中译英和英译中测试。

能够正常完成即证明运行链路不依赖外部 API。应用正常运行时只访问：

```text
127.0.0.1:18765  本地 faster-whisper + OPUS-MT 服务
```

可用以下命令检查本机监听端口：

```powershell
Get-NetTCPConnection -State Listen |
  Where-Object LocalPort -eq 18765 |
  Select-Object LocalAddress,LocalPort,OwningProcess
```

## 9. 常见问题排查

### 9.1 点击启动后提示找不到 Python

```powershell
python --version
where.exe python
python -c "import faster_whisper; print('OK')"
```

如果这些命令失败，重新安装 Python 3.11，确保 PATH 正确，并重新安装 faster-whisper。

### 9.2 提示本地语音识别服务启动失败

常见原因是 Whisper 模型没有提前下载，或缓存被清理。重新联网执行：

```powershell
$env:HF_HUB_OFFLINE='0'
python -c "from faster_whisper import WhisperModel; WhisperModel('small', device='cpu', compute_type='int8')"
```

也可检查 18765 端口是否被其他程序占用：

```powershell
Get-NetTCPConnection -LocalPort 18765 -ErrorAction SilentlyContinue
```

### 9.3 提示缺少 OPUS-MT 模型

```powershell
powershell -ExecutionPolicy Bypass -File .\setup_local_models.ps1
```

确认 `%LOCALAPPDATA%\VoiceScreen\Models` 下同时存在 `opus-mt-zh-en-ct2-int8`、`opus-mt-en-zh-ct2-int8` 与 `opus-mt-th-en-ct2-int8`，并且各自包含 `model.bin`。模型准备中途失败时，不要手动拼接文件；保留下载缓存并重新运行脚本即可续传，脚本会自动重试最多 3 次。

### 9.4 程序检测不到 CABLE Input

- 确认 VB-CABLE 驱动安装后已经重启 Windows。
- 在“更多声音设置”里启用被禁用的 `CABLE Input` 和 `CABLE Output`。
- 在 VoiceScreen 中点击“刷新设备”。
- 不要选择名称相反的端点：程序播放端是 `CABLE Input`，Discord 录音端是 `CABLE Output`。

### 9.5 对方听不到任何声音

- Discord 麦克风必须为 `CABLE Output`。
- VoiceScreen 必须已经点击“启动”。
- 不按右 Alt 直接说中文，先确认 Discord 能收到原声输入电平；再按右 Alt 完成一次真实翻译发送。
- 检查 Discord 是否启用了按键说话；若启用，播放 TTS 时也必须满足 Discord 的按键条件。
- 检查 Discord 输入音量和 Windows 的 CABLE Output 录音音量是否为零。

### 9.6 对方能听见中文，但听不见英文

- 点击“翻译并试听（仅耳机）”，先确认本地翻译和英文 TTS 正常；然后用右 Alt 完成一次真实 Discord 发送测试。
- 安装 Windows 英文语音并重启程序。
- 暂时关闭 Discord 的 Krisp、自动增益和噪声抑制。
- 查看悬浮窗是否出现“已发送”英文；没有则查看日志。

### 9.7 英文缺少开头或句尾

- 暂时关闭 Discord 降噪和自动输入灵敏度。
- 将 Discord 输入灵敏度改成手动并适当降低阈值。
- 确保没有同时运行会处理虚拟麦克风的声卡软件。
- 程序已加入尾部静音和 WASAPI 排空保护；若仍稳定缺词，请保留测试句和日志用于定位。

### 9.8 有回声或自己听到自己的声音

依次检查：

1. Discord 扬声器必须是 HyperX 实体耳机，不能是 `CABLE Input`。
2. Discord 麦克风必须是 `CABLE Output`。
3. Windows 的 CABLE Output 属性中“侦听此设备”必须关闭。
4. HyperX 麦克风的“侦听此设备”也必须关闭。
5. 使用耳机，不要外放。
6. 退出其他音频路由软件，避免它们再次把 CABLE Output 回送到耳机或 CABLE Input。

### 9.9 游戏有声音时也出现字幕

当前版本不会回退到全系统捕获。如果发生：

- 确认运行的是本仓库最新版。
- 确认字幕确实来自 VoiceScreen，而不是 Discord 游戏串流或其他字幕软件。
- 检查游戏声音是否通过某个机器人、直播或屏幕共享重新进入了 Discord 进程。
- 保存日志并记录复现步骤。

### 9.10 CPU 占用或延迟较高

- 首次启动的模型加载不代表长期占用。
- 关闭其他 CPU 密集程序。
- 保持项目提供的 OPUS-MT INT8 模型，不要自行替换目录中的权重和 tokenizer。
- 当前版本强制 CPU 推理以保护 3A 游戏显卡；低性能 CPU 上可能超过 5 秒。
- 不要同时启动多个 VoiceScreen 实例。

### 9.11 查看历史和调整悬浮窗

- VoiceScreen 运行时，按键盘 `PgUp` 查看更早的字幕，按 `PgDn` 向最新字幕翻页；回到底部后会继续自动跟随新字幕。
- 程序在内存中保留最近 200 条字幕，退出后不会保存字幕内容。
- 点击主界面的“① 解锁移动/缩放”，看到悬浮窗顶部黄色提示后，拖动顶部移动位置，拖动右下角的黄色标记调整大小。
- 完成后点击“② 完成并锁定”，恢复鼠标穿透，避免在游戏中挡住点击。位置和尺寸会自动保存。
- 主界面的“字幕字号”滑块可在 14–42 之间即时调整并保存。

### 9.12 多人同时说话

当前 Windows Process Loopback 捕获的是 Discord 输出的混合音轨，Discord 没有把参与者用户名交给 VoiceScreen。因此当前字幕不能可靠标出真实说话人。后续可以增加纯本地说话人聚类并显示“说话人 1/2”，但它无法知道真实用户名，而且重叠说话时准确率有限。若必须显示 Discord 用户名，需要改成 Discord Bot 分用户接收音轨。

## 10. 日志与配置位置

运行日志：

```text
%LOCALAPPDATA%\VoiceScreen\voicescreen.log
```

打开日志：

```powershell
notepad "$env:LOCALAPPDATA\VoiceScreen\voicescreen.log"
```

当前用户的加密设置：

```text
%LOCALAPPDATA%\VoiceScreen\settings.dat
```

如果设备配置异常，可以在 VoiceScreen 退出后重置设置：

```powershell
Remove-Item "$env:LOCALAPPDATA\VoiceScreen\settings.dat" -ErrorAction SilentlyContinue
```

这不会删除 Whisper 或 OPUS-MT 模型。

## 11. 更新、迁移与卸载

### 11.1 更新源码部署

```powershell
Set-Location "$env:USERPROFILE\Desktop\VoiceScreen"
git pull --ff-only
dotnet restore VoiceScreen.sln
dotnet publish src\VoiceScreen.App\VoiceScreen.App.csproj `
  -c Release -r win-x64 --self-contained false `
  -o dist\VoiceScreen-local-offline
```

更新前先退出 VoiceScreen。不要在程序运行时覆盖发布目录。

### 11.2 迁移到另一台电脑

可以复制整个发布目录，但 Python 包、Whisper 缓存、OPUS-MT 模型、Windows 英文语音和 VB-CABLE 驱动必须在新电脑上重新安装。`settings.dat` 使用 Windows 当前用户加密，不应复制到另一台电脑或另一个账号。

### 11.3 卸载

1. 退出 VoiceScreen。
2. 删除 VoiceScreen 发布目录。
3. 可选删除配置与日志：

   ```powershell
   Remove-Item "$env:LOCALAPPDATA\VoiceScreen" -Recurse -Force
   ```

4. 在“设置 → 应用 → 已安装的应用”中按需卸载 Python、.NET Desktop Runtime 和 VB-CABLE。
5. 本地模型位于 `%LOCALAPPDATA%\VoiceScreen\Models`；如需释放空间，请确认不再使用后再删除该目录。

## 12. 官方下载与文档

- [.NET Windows 安装文档](https://learn.microsoft.com/dotnet/core/install/windows)
- [Python 3.11.9 Windows 发布页](https://www.python.org/downloads/release/python-3119/)
- [Helsinki-NLP OPUS-MT 中译英模型](https://huggingface.co/Helsinki-NLP/opus-mt-zh-en)
- [Helsinki-NLP OPUS-MT 英译中模型](https://huggingface.co/Helsinki-NLP/opus-mt-en-zh)
- [Helsinki-NLP OPUS-MT 泰译英模型](https://huggingface.co/Helsinki-NLP/opus-mt-th-en)
- [CTranslate2 官方项目](https://github.com/OpenNMT/CTranslate2)
- [VB-Audio Virtual Cable 官方页面](https://vb-audio.com/Cable/)
- [Discord 语音与视频故障排查](https://support.discord.com/hc/articles/360045138471)

不要从第三方软件下载站获取 Python 或虚拟声卡驱动。
