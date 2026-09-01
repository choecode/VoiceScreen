# VoiceScreen 部署与运维指南

本文以当前推荐架构为主：

- Windows 11 x64 运行 VoiceScreen 客户端；
- 28 号 NVIDIA DGX Spark 运行 Qwen ASR、翻译和私人音色 TTS；
- Windows Process Loopback 只捕获选定应用；
- VB-CABLE 只在需要向 Discord 发送英文语音时使用。

当前验证日期：**2026-08-25**。

## 1. 先选择部署模式

| 模式 | Windows 需要 | 模型位置 | 适用情况 |
|---|---|---|---|
| Spark 推荐模式 | VoiceScreen；发送语音时另装 VB-CABLE | DGX Spark | 当前质量和可用性最佳 |
| 仅字幕模式 | VoiceScreen | Spark 或本机 | 不需要麦克风、Windows 英文音色和 VB-CABLE |
| Windows 本机回退 | VoiceScreen、Python 3.11、本机模型 | Windows | Spark 不可用时离线应急 |
| 在线备用 | VoiceScreen、互联网 | MyMemory / Edge TTS | 只用于临时对照，不是生产 SLA |

只需要看 YouTube、浏览器、Discord 或播放器字幕时，建议先关闭“同时把麦克风中文翻译成英文并发送到语音应用”。这样 VB-CABLE 缺失也不会影响启动。

## 2. 网络与安全前提

Windows 客户端必须能够访问 Spark：

```powershell
Test-NetConnection spark-host.local -Port 18765
Test-NetConnection spark-host.local -Port 18766
```

两个命令的 `TcpTestSucceeded` 都应为 `True`。

端口用途：

| 端口 | 用途 | 是否生产默认 |
|---:|---|---|
| `18765/tcp` | Qwen3-ASR、Qwen3-4B 翻译和语义断句 | 是 |
| `18766/tcp` | Qwen3-TTS 私人音色 | 是 |
| `18767/tcp` | CosyVoice A/B 实验 | 否 |

服务接口没有内建 TLS。只允许可信局域网访问，并配置随机令牌；不要在路由器上做公网端口转发。跨网络使用时应先建立 VPN，或在前面部署带 TLS 和访问控制的反向代理。

## 3. Spark 主机要求

当前实机：

- NVIDIA DGX Spark，`aarch64`；
- 128 GB 统一内存；
- 支持 GPU 的 Docker 与 Docker Compose v2；
- 模型根目录由 `VOICESCREEN_MODEL_ROOT` 配置，示例为 `/opt/voicescreen/models`；
- 生产容器使用 NGC ARM64 CUDA/PyTorch 基础镜像。

模型目录及当前磁盘占用约为：

```text
<SPARK_MODEL_ROOT>/Qwen3-ASR-1.7B             4.4 GB
<SPARK_MODEL_ROOT>/Qwen3-4B-Instruct-2507    7.6 GB
<SPARK_MODEL_ROOT>/Qwen3-TTS-12Hz-0.6B-Base  2.4 GB
<SPARK_PROFILE_ROOT>                          私人录音与 prompt
```

至少预留 25 GB 磁盘，给模型、Docker 构建层和升级期间的旧镜像留出余量。

## 4. 在 Spark 准备模型

中国大陆网络优先使用 ModelScope。开始大文件下载前，先确认到镜像的连通性，并避免流量意外走海外代理：

```bash
curl -I --max-time 10 https://modelscope.cn
env | grep -i proxy
```

如环境中存在并非你有意设置的 `HTTP_PROXY`、`HTTPS_PROXY` 或 `ALL_PROXY`，先在当前下载终端取消。已经开始的可续传下载不要仅为了切换镜像而重来。

安装 ModelScope 下载工具：

```bash
python3 -m venv /opt/voicescreen/venvs/download
source /opt/voicescreen/venvs/download/bin/activate
python -m pip install -i https://pypi.tuna.tsinghua.edu.cn/simple -U modelscope
```

下载三个生产模型：

```bash
mkdir -p /opt/voicescreen/models

modelscope download --model Qwen/Qwen3-ASR-1.7B \
  --local_dir /opt/voicescreen/models/Qwen3-ASR-1.7B

modelscope download --model Qwen/Qwen3-4B-Instruct-2507 \
  --local_dir /opt/voicescreen/models/Qwen3-4B-Instruct-2507

modelscope download --model Qwen/Qwen3-TTS-12Hz-0.6B-Base \
  --local_dir /opt/voicescreen/models/Qwen3-TTS-12Hz-0.6B-Base
```

下载后检查目录，不要只相信命令退出码：

```bash
du -sh /opt/voicescreen/models/Qwen3-*
find /opt/voicescreen/models/Qwen3-ASR-1.7B -name '*.safetensors' -type f
find /opt/voicescreen/models/Qwen3-4B-Instruct-2507 -name '*.safetensors' -type f
find /opt/voicescreen/models/Qwen3-TTS-12Hz-0.6B-Base -name '*.safetensors' -type f
```

只有 ModelScope 缺少对应文件、镜像版本不完整或校验失败时，才改用 Hugging Face 上游，并记录原因。

## 5. 准备私人音色

### 5.1 参考录音要求

- 一个人说话；
- 5–20 秒为宜；
- 无背景音乐、混响和明显底噪；
- 正常音量、自然语速；
- 参考文本必须与录音逐字一致；
- 当前服务合成英文，所以推荐使用清晰英文参考录音。

把 M4A 转成 24 kHz 单声道 WAV：

```bash
mkdir -p /opt/voicescreen/voice-profiles
ffmpeg -i your-reference-recording.m4a -ar 24000 -ac 1 -c:a pcm_s16le \
  /opt/voicescreen/voice-profiles/my-voice-reference.wav
```

将参考文本写入 `.env` 的 `VOICESCREEN_VOICE_REFERENCE_TEXT`，必须与录音逐字一致。例如：

```text
Replace this example with the exact transcript of your reference recording.
```

如果录音内容不同，必须同步修改 `VOICESCREEN_VOICE_REFERENCE_TEXT`。不要用近似文本，否则音色、韵律和清晰度都会下降。

服务首次启动时会生成：

```text
/opt/voicescreen/voice-profiles/my-voice.pt
```

更换录音或参考文本后，需要先停止 TTS 服务，删除旧的 `my-voice.pt`，再启动服务重新生成。这个文件和参考录音都属于私人数据，不应提交到 Git 或放入 Windows 发布包。

## 6. 部署 Spark 服务

在 Spark 上取得当前仓库代码：

```bash
cd /opt/voicescreen
git clone https://github.com/choecode/VoiceScreen.git
cd /opt/voicescreen/VoiceScreen/deploy/spark
```

已有仓库时：

```bash
cd /opt/voicescreen/VoiceScreen
git pull --ff-only
cd deploy/spark
```

创建只允许当前用户读取的令牌文件：

```bash
umask 077
TOKEN="$(openssl rand -hex 32)"
printf 'VOICESCREEN_API_TOKEN=%s\n' "$TOKEN" > .env
printf '把下面令牌填入 Windows VoiceScreen 高级设置：\n%s\n' "$TOKEN"
unset TOKEN
```

构建使用清华 PyPI 镜像；Compose 已把该地址作为默认构建参数。只构建生产服务：

```bash
docker compose build model-service tts-service
docker compose up -d model-service tts-service
```

不要为普通生产启动添加 `--profile experiments`。

### 6.1 与其他 SGLang 模型共存

如果同一台 Spark 还运行 Qwen 27B 等 SGLang 服务，必须为 VoiceScreen 预留显存。当前验证的共存配置把 SGLang 的静态显存比例从 `0.75` 调为 `0.60`：

```yaml
command:
  - --mem-fraction-static
  - "0.60"
```

修改后需要重建 SGLang 容器，而不是只重启进程：

```bash
docker compose -f <SGLANG_COMPOSE_FILE> up -d --force-recreate inference
```

等待 SGLang 的模型接口可用后，再启动 VoiceScreen 的 `model-service`。验收顺序：

```bash
curl -fsS http://127.0.0.1:8888/model_info
curl -fsS http://127.0.0.1:18765/health
curl -fsS http://127.0.0.1:18766/health
```

`--mem-fraction-static` 不是所有 SGLang 版本都使用的参数；如果启动器名称不同，请使用该版本等价的静态 KV-cache 显存参数。不要盲目降得过低，必须同时确认 27B 服务的上下文长度和吞吐仍满足需求。

查看状态：

```bash
docker compose ps
docker compose logs --tail=100 model-service
docker compose logs --tail=100 tts-service
curl -fsS http://127.0.0.1:18765/health
curl -fsS http://127.0.0.1:18766/health
```

健康响应应包含 `"ready": true`，ASR 设备和 TTS 设备应为 `spark-gpu`。首次加载可能需要数分钟，Compose 健康检查已经为模型加载预留启动时间。

当前运行部署的 Compose 配置位于：

```text
/opt/voicescreen/voicescreen-services/model-service/compose.yaml
```

如果改用仓库目录部署，应只保留一套同名容器，避免两个 Compose 项目争用 `18765/18766`。

## 7. 可选：CosyVoice 实验服务

CosyVoice 不是生产依赖。只有做 A/B 时才执行：

```bash
docker compose --profile experiments build cosyvoice-service
docker compose --profile experiments up -d cosyvoice-service
curl -fsS http://127.0.0.1:18767/health
```

它要求：

- `/opt/voicescreen/voicescreen-experiments/CosyVoice` 中存在固定版本的官方源码；
- `${VOICESCREEN_MODEL_ROOT}/Fun-CosyVoice3-0.5B-2512` 中存在模型；
- 额外约 9 GB 模型磁盘与运行显存。

测试完成后停止，释放 GPU 资源：

```bash
docker compose --profile experiments stop cosyvoice-service
```

## 8. 构建 Windows 客户端

推荐生成自包含 win-x64 目录，这样目标机不需要单独安装 .NET Runtime。源码构建机需要 .NET 8 SDK。

中国大陆网络可使用华为云 NuGet 镜像：

```powershell
Set-Location "$env:USERPROFILE\Desktop\VoiceScreen"

dotnet restore src\VoiceScreen.App\VoiceScreen.App.csproj `
  -r win-x64 `
  --source https://mirrors.huaweicloud.com/repository/nuget/v3/index.json

dotnet publish src\VoiceScreen.App\VoiceScreen.App.csproj `
  -c Release -r win-x64 --self-contained true --no-restore `
  -o dist\VoiceScreen-release
```

启动：

```powershell
.\dist\VoiceScreen-release\VoiceScreen.App.exe
```

发布目录必须整体保留。至少应包含：

```text
VoiceScreen.App.exe
VoiceScreen.App.dll
VoiceScreen.Core.dll
Microsoft.ML.OnnxRuntime.dll
Models\silero_vad_16k_op15.onnx
LocalService\local_outgoing_service.py
THIRD_PARTY_NOTICES.md
```

不要只把 `VoiceScreen.App.exe` 拖到桌面。若需要桌面入口，应创建快捷方式。

## 9. Windows 音频准备

### 9.1 仅字幕

仅字幕不要求 VB-CABLE、实体麦克风或 Windows 英文音色。打开 VoiceScreen 后关闭发送复选框即可。

### 9.2 发送英文到 Discord

1. 从 [VB-Audio 官方网站](https://vb-audio.com/Cable/) 下载 VB-CABLE。
2. 以管理员身份运行 `VBCABLE_Setup_x64.exe`。
3. 安装后重启 Windows，不能只注销。
4. 在 Windows“更多声音设置”中确认 `CABLE Input` 和 `CABLE Output` 已启用。
5. 关闭 `CABLE Output` 属性中的“侦听此设备”。

设备方向很容易看反：

| 位置 | 正确设备 |
|---|---|
| VoiceScreen 虚拟音频线 | `CABLE Input (VB-Audio Virtual Cable)` |
| Discord 输入设备 | `CABLE Output (VB-Audio Virtual Cable)` |
| Discord 输出设备 | 实体耳机 |
| VoiceScreen 本地监听输出 | 同一实体耳机 |

Windows 本地音色只在不使用 Spark 私人音色时需要。在“设置 → 时间和语言 → 语音”中安装英文语音包，然后重启 VoiceScreen。

## 10. 首次配置 Windows 客户端

1. 先启动要监听的应用。
2. 打开 VoiceScreen，在“监听应用”中选中准确的应用和 PID。
3. 只看字幕时关闭发送复选框；需要双向翻译时保持开启。
4. 展开高级设置。
5. 语音识别选择 `Spark Qwen3-ASR 1.7B（推荐）`。
6. 翻译选择 `Spark Qwen3-4B（推荐）`。
7. Spark 服务地址填写你的 Spark 地址，例如 `http://spark-host.local:18765/`。
8. 填入 Spark `.env` 中同一个访问令牌；令牌会在当前 Windows 用户下加密保存。
9. 需要发送时，选择实体麦克风、本地耳机和私人音色；确认虚拟音频线自动选中 `CABLE Input`。
10. 点击“开始监听”，等待顶部状态变成“运行中”。

TTS 地址不单独配置：客户端会从 Spark 地址取主机，并自动连接同一主机的 `18766`。

如果目标应用以管理员身份运行而 VoiceScreen 无法捕获，可使用高级设置中的“以管理员身份重启”。两者权限级别尽量保持一致。

## 11. 日常验收

### 11.1 字幕链路

1. 在目标应用中播放清晰英文。
2. VoiceScreen 状态应显示持续收到目标进程音频，而不是“未检测到声音”。
3. 悬浮窗先出现实时英文和中文，停顿后进入历史。
4. 连续播放背景音乐时，不应持续生成无意义字幕。
5. 使用鼠标滚轮或 `PgUp` 浏览旧字幕，确认新内容不会强行把视图拉到底部。
6. 回到底部后，确认字幕重新自动跟随。

### 11.2 发送链路

1. Discord 麦克风测试中，普通说中文，对方应听见中文原声。
2. 按住右 Alt 说完整中文，松开。
3. 悬浮窗应显示“我说”和“已发送”。
4. 对方应听见完整英文，特别检查开头和最后一个单词。
5. 播放结束后再次普通说话，确认中文原声已恢复。
6. 长中文应在第一段英文生成后开始播放，而不是等待整段音频完成。

## 12. Windows 本机回退部署

本机回退要求 Python 3.11 x64，且 `python` 必须能从普通 PowerShell 直接调用：

```powershell
python --version
where.exe python
```

准备 Whisper 和 OPUS-MT：

```powershell
$env:HF_ENDPOINT='https://hf-mirror.com'
powershell -ExecutionPolicy Bypass -File .\tools\setup_local_models.ps1
```

可选安装 Sherpa-ONNX：

```powershell
$env:HF_ENDPOINT='https://hf-mirror.com'
powershell -ExecutionPolicy Bypass -File .\tools\setup_local_models.ps1 -Sherpa
```

下载前先确认镜像可达并未使用非预期海外代理。若镜像缺文件或校验失败，再改用官方 Hugging Face。

模型位于：

```text
%LOCALAPPDATA%\VoiceScreen\Models
```

在高级设置中把 ASR 改为本地 Whisper 或 Sherpa，并将本地音色改为 Windows 英文音色。本机服务会由 VoiceScreen 自动启动，监听 `127.0.0.1:18765`；它与 Spark 服务不会同时占用 Windows 本机端口。

## 13. 更新

### 13.1 Spark

```bash
cd /opt/voicescreen/VoiceScreen
git pull --ff-only
cd deploy/spark
docker compose build model-service tts-service
docker compose up -d model-service tts-service
docker compose ps
```

不要删除模型和 `voice-profiles` 卷。镜像更新后先确认两个健康检查，再清理旧镜像。

### 13.2 Windows

更新前先退出 VoiceScreen，不要覆盖正在运行的发布目录：

```powershell
git pull --ff-only
powershell -ExecutionPolicy Bypass -File .\tools\run_tests.ps1
dotnet restore src\VoiceScreen.App\VoiceScreen.App.csproj -r win-x64
dotnet publish src\VoiceScreen.App\VoiceScreen.App.csproj `
  -c Release -r win-x64 --self-contained true --no-restore `
  -o dist\VoiceScreen-release-new
```

从新目录启动验证成功后，再更新快捷方式。设置和日志保存在 `%LOCALAPPDATA%\VoiceScreen`，不在发布目录中。

## 14. 常见问题

### 14.1 提示找不到 CABLE Input

- 只看字幕：关闭发送复选框，然后开始监听。
- 需要发送：确认安装驱动后已经重启 Windows；在声音设置中启用 CABLE Input/Output；点“刷新列表”。
- VoiceScreen 选择的是播放端 `CABLE Input`，Discord 选择的是录音端 `CABLE Output`。

### 14.2 一直停在加载模型

Windows：

```powershell
Test-NetConnection spark-host.local -Port 18765
Test-NetConnection spark-host.local -Port 18766
```

Spark：

```bash
docker ps --filter name=voicescreen
docker logs --tail=200 voicescreen-model-service
docker logs --tail=200 voicescreen-tts-service
curl -fsS http://127.0.0.1:18765/health
curl -fsS http://127.0.0.1:18766/health
```

`401 Unauthorized` 表示 Windows 与 Spark 令牌不一致；`503` 表示模型尚未加载完成或启动失败。

### 14.3 应用显示运行中，但没有字幕

- 确认选中的 PID 正在实际播放声音；浏览器重启后 PID 会变化，需要刷新列表。
- 状态栏若显示“5 秒未检测到声音”，说明捕获已经建立，但当前目标是静音。
- 不要选择浏览器启动器、更新器或无声音的辅助进程。
- 目标以管理员身份运行时，让 VoiceScreen 使用相同权限。
- 查看日志中的 `Process loopback metrics`；`peakPcm16` 长期接近 0 说明目标没有输出音频。

### 14.4 延迟越来越高

- 确认 Spark GPU 没有同时跑 CosyVoice 或其他大模型实验。
- 检查局域网丢包和 Spark 负载。
- 长文本应看到 `Outgoing TTS chunk` 日志；如果整句只有一个超长块，保留文本样本用于调整分块。
- 不要同时运行多个 VoiceScreen 客户端请求同一个单 worker TTS 服务。
- 临时字幕队列会自动丢弃过期预览；若永久历史仍持续落后，应保存日志中的 `end-to-end` 和 `translation` 指标。

### 14.5 对方只听到英文开头

- 暂时关闭 Discord Krisp、自动增益和噪声抑制。
- 把 Discord 输入灵敏度改为手动并适当降低阈值。
- 确认没有其他软件同时处理 VB-CABLE。
- 检查 VoiceScreen 日志是否出现 TTS 超时、后续块失败或播放排空超时。

### 14.6 只有一个音色

- 私人音色由 Spark 提供，默认只有一个 `my-voice` profile。
- 额外 Windows 音色需要在 Windows 语音设置中安装，重启应用后才会枚举。
- Edge TTS 音色位于单独的在线音色下拉框，不会混入本地音色列表。

### 14.7 重复翻译或历史内容丢失

- 使用当前 `main` 构建，旧版本曾把临时结果错误追加到历史。
- 临时区域允许反复修改，历史区只保存最终结果；两者视觉上靠近但含义不同。
- 用户手动滚动时只是暂停自动跟随，不会停止新字幕写入。
- 保留出现问题前后的日志和原始语句，重点查 `utteranceId`、`final subtitle` 和语义边界记录。

### 14.8 Windows 安全中心阻止 DLL

当前发布包未做商业代码签名，Windows 可能提示无法确认 DLL 发布者。只在确认包来自本仓库或自己构建后，对完整发布目录解除下载标记：

```powershell
Get-ChildItem -LiteralPath 'C:\path\to\VoiceScreen-release' -Recurse -File |
  Unblock-File
```

不要从第三方网盘或下载站获取修改版。正式外部分发应使用可信代码签名证书签名 EXE 和 DLL。

### 14.9 提示缺少 VoiceScreen.Core 或 ONNX Runtime

说明发布目录不完整。重新复制或重新发布整个目录，不要只复制 EXE。确认 `VoiceScreen.Core.dll`、`Microsoft.ML.OnnxRuntime.dll`、`onnxruntime.dll` 和 `Models\silero_vad_16k_op15.onnx` 都存在。

### 14.10 回声或听见自己的延迟声音

1. Discord 输出必须是实体耳机。
2. Discord 输入必须是 `CABLE Output`。
3. Windows 的 CABLE Output“侦听此设备”必须关闭。
4. 使用耳机，不要外放。
5. 退出其他虚拟混音、直播和声卡路由软件后复测。

## 15. 日志、设置与卸载

日志：

```text
%LOCALAPPDATA%\VoiceScreen\voicescreen.log
```

打开：

```powershell
notepad "$env:LOCALAPPDATA\VoiceScreen\voicescreen.log"
```

加密设置：

```text
%LOCALAPPDATA%\VoiceScreen\settings.dat
```

重置设置前先退出应用：

```powershell
Remove-Item "$env:LOCALAPPDATA\VoiceScreen\settings.dat" -ErrorAction SilentlyContinue
```

卸载 Windows 客户端时删除发布目录；若也不再需要配置和日志，可删除 `%LOCALAPPDATA%\VoiceScreen`。删除 Spark 模型、私人音色或 Docker 卷属于独立操作，不应在普通客户端卸载时执行。

## 16. 官方项目

- [Qwen3-ASR](https://github.com/QwenLM/Qwen3-ASR)
- [Qwen3-TTS](https://github.com/QwenLM/Qwen3-TTS)
- [Qwen3-4B-Instruct-2507](https://huggingface.co/Qwen/Qwen3-4B-Instruct-2507)
- [Silero VAD](https://github.com/snakers4/silero-vad)
- [CosyVoice](https://github.com/FunAudioLLM/CosyVoice)
- [VB-Audio Virtual Cable](https://vb-audio.com/Cable/)
- [.NET Windows 安装文档](https://learn.microsoft.com/dotnet/core/install/windows)
