[CmdletBinding()]
param(
    # Installs the optional Sherpa-ONNX Zipformer ASR backend: the Python package
    # AND the model files. Both are required; installing only the package leaves
    # the "Sherpa-ONNX Zipformer" option in the UI selectable but broken.
    [switch]$Sherpa
)

$ErrorActionPreference = "Stop"

Write-Host "VoiceScreen local model setup" -ForegroundColor Cyan
Write-Host "This downloads Whisper base/small and three OPUS-MT translation models once."

# -Sherpa is the documented way; the environment variable is kept for existing callers.
$installSherpa = $Sherpa.IsPresent -or
    [string]::Equals($env:VOICESCREEN_SETUP_SHERPA, "1", [System.StringComparison]::OrdinalIgnoreCase)

if (-not (Get-Command python -ErrorAction SilentlyContinue)) {
    throw "Python was not found. Install Python 3.11 x64 and reopen PowerShell."
}

$pythonVersion = python --version
Write-Host $pythonVersion

python -m pip install --index-url https://pypi.org/simple `
    "faster-whisper==1.2.1" `
    "ctranslate2==4.5.0" `
    "transformers==4.46.3" `
    "sentencepiece==0.2.1" `
    "sacremoses==0.1.1" `
    "edge-tts==7.2.7" `
    "mutagen==1.47.0" `
    "noisereduce>=3.0,<4" `
    "huggingface-hub>=0.34,<1"
if ($LASTEXITCODE -ne 0) { throw "Failed to install runtime packages." }
if ($installSherpa) {
    python -m pip install --index-url https://pypi.org/simple "sherpa-onnx>=1.11.0"
    if ($LASTEXITCODE -ne 0) { throw "Failed to install sherpa-onnx package." }
    Write-Host "Sherpa-ONNX package installed." -ForegroundColor Green
} else {
    Write-Host "Skipping the optional Sherpa-ONNX backend. Rerun with -Sherpa to install it." -ForegroundColor Yellow
}

$env:HF_HUB_OFFLINE = "0"
$env:HF_HUB_DISABLE_XET = "1"
$modelRoot = Join-Path $env:LOCALAPPDATA "VoiceScreen\Models"
New-Item -ItemType Directory -Force -Path $modelRoot | Out-Null

$zhEnReady = Test-Path (Join-Path $modelRoot "opus-mt-zh-en-ct2-int8\model.bin")
$enZhReady = Test-Path (Join-Path $modelRoot "opus-mt-en-zh-ct2-int8\model.bin")
$thEnReady = Test-Path (Join-Path $modelRoot "opus-mt-th-en-ct2-int8\model.bin")
if (-not ($zhEnReady -and $enZhReady -and $thEnReady)) {
    # Torch is only used by the one-time official Transformers -> CTranslate2 conversion.
    # 2.6+ is required because older torch.load versions have a known security issue.
    python -m pip install --index-url https://pypi.org/simple "torch==2.6.0"
    if ($LASTEXITCODE -ne 0) { throw "Failed to install the safe CPU build of PyTorch." }
}

Write-Host "Downloading Whisper base (realtime preview) and small (final result)..." -ForegroundColor Cyan
$env:HF_HUB_OFFLINE = "1"
python -c "from faster_whisper import WhisperModel; [WhisperModel(name, device='cpu', compute_type='int8', local_files_only=True) for name in ('base','small')]; print('Whisper base/small already cached')"
if ($LASTEXITCODE -ne 0) {
    $env:HF_HUB_OFFLINE = "0"
    $whisperReady = $false
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        python -c "from faster_whisper import WhisperModel; [WhisperModel(name, device='cpu', compute_type='int8') for name in ('base','small')]; print('Whisper base/small ready')"
        if ($LASTEXITCODE -eq 0) {
            $whisperReady = $true
            break
        }
        if ($attempt -lt 3) { Start-Sleep -Seconds 2 }
    }
    if (-not $whisperReady) { throw "Failed to download Whisper base/small after 3 attempts. Check the network and rerun this script to resume." }
}
$env:HF_HUB_OFFLINE = "0"

function Install-OpusModel {
    param(
        [Parameter(Mandatory = $true)][string]$Repository,
        [Parameter(Mandatory = $true)][string]$Name
    )

    $source = Join-Path $modelRoot "$Name-source"
    $target = Join-Path $modelRoot "$Name-ct2-int8"
    if (Test-Path (Join-Path $target "model.bin")) {
        Write-Host "$Name is already ready; skipping." -ForegroundColor Green
        return
    }

    New-Item -ItemType Directory -Force -Path $source | Out-Null
    Write-Host "Downloading $Repository..." -ForegroundColor Cyan
    $downloaded = $false
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        hf download $Repository --local-dir $source --include `
            config.json generation_config.json pytorch_model.bin source.spm target.spm vocab.json tokenizer_config.json
        if ($LASTEXITCODE -eq 0) {
            $downloaded = $true
            break
        }
        if ($attempt -lt 3) {
            Write-Host "Download interrupted; retrying $Repository ($attempt/3)..." -ForegroundColor Yellow
            Start-Sleep -Seconds 2
        }
    }
    if (-not $downloaded) { throw "Failed to download $Repository after 3 attempts." }

    if (Test-Path $target) {
        throw "Incomplete target already exists: $target. Rename it and rerun this script."
    }

    Write-Host "Converting $Name to CPU INT8..." -ForegroundColor Cyan
    ct2-transformers-converter --model $source --output_dir $target --quantization int8 `
        --copy_files source.spm target.spm vocab.json tokenizer_config.json
    if ($LASTEXITCODE -ne 0) { throw "Failed to convert $Repository." }
}

Install-OpusModel -Repository "Helsinki-NLP/opus-mt-zh-en" -Name "opus-mt-zh-en"
Install-OpusModel -Repository "Helsinki-NLP/opus-mt-en-zh" -Name "opus-mt-en-zh"
Install-OpusModel -Repository "Helsinki-NLP/opus-mt-th-en" -Name "opus-mt-th-en"

function Install-SherpaZipformer {
    # Streaming bilingual (Chinese + English) Zipformer transducer from the k2-fsa project.
    $repository = "csukuangfj/sherpa-onnx-streaming-zipformer-bilingual-zh-en-2023-02-20"
    $name = "sherpa-onnx-streaming-zipformer-bilingual-zh-en-2023-02-20"
    $target = Join-Path $modelRoot $name

    # Only the INT8 variants are fetched. The repository also ships FP32 copies of the
    # same three graphs, but local_outgoing_service.py picks the first file matching
    # *encoder*.onnx / *decoder*.onnx / *joiner*.onnx -- keeping exactly one candidate
    # per role makes that selection unambiguous instead of dependent on sort order.
    # It also cuts the download from roughly 700 MB to 190 MB.
    $files = @(
        "tokens.txt",
        "encoder-epoch-99-avg-1.int8.onnx",
        "decoder-epoch-99-avg-1.int8.onnx",
        "joiner-epoch-99-avg-1.int8.onnx"
    )

    $missing = @($files | Where-Object { -not (Test-Path (Join-Path $target $_)) })
    if ($missing.Count -eq 0) {
        Write-Host "Sherpa-ONNX Zipformer model is already ready; skipping." -ForegroundColor Green
        return
    }

    New-Item -ItemType Directory -Force -Path $target | Out-Null
    Write-Host "Downloading $repository (about 190 MB)..." -ForegroundColor Cyan
    $downloaded = $false
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        hf download $repository --local-dir $target --include $files
        if ($LASTEXITCODE -eq 0) {
            $downloaded = $true
            break
        }
        if ($attempt -lt 3) {
            Write-Host "Download interrupted; retrying $repository ($attempt/3)..." -ForegroundColor Yellow
            Start-Sleep -Seconds 2
        }
    }
    if (-not $downloaded) { throw "Failed to download $repository after 3 attempts." }

    # Verify rather than trust the exit code: a partial download here surfaces much
    # later as an opaque "Sherpa-ONNX Zipformer local model was not found" at app start.
    $stillMissing = @($files | Where-Object { -not (Test-Path (Join-Path $target $_)) })
    if ($stillMissing.Count -gt 0) {
        throw "Sherpa model download incomplete. Missing: $($stillMissing -join ', ')"
    }

    Write-Host "Sherpa-ONNX Zipformer model ready: $target" -ForegroundColor Green
}

if ($installSherpa) {
    $env:HF_HUB_OFFLINE = "0"
    Install-SherpaZipformer

    # Load it once now so a broken install fails here, with this script's context,
    # instead of inside the app behind a generic startup error.
    Write-Host "Verifying the Sherpa-ONNX backend can load the model..." -ForegroundColor Cyan
    $env:VOICESCREEN_MODEL_ROOT = $modelRoot
    python -c "import sys, importlib.util; sys.path.insert(0, r'$PSScriptRoot\..\src\VoiceScreen.App\LocalService'); spec = importlib.util.spec_from_file_location('svc', r'$PSScriptRoot\..\src\VoiceScreen.App\LocalService\local_outgoing_service.py'); m = importlib.util.module_from_spec(spec); spec.loader.exec_module(m); m.initialize_sherpa_asr(); print('Sherpa-ONNX Zipformer loaded successfully')"
    if ($LASTEXITCODE -ne 0) { throw "The Sherpa-ONNX backend failed to load the downloaded model." }
}

$env:HF_HUB_OFFLINE = "1"
Write-Host "All VoiceScreen local models are ready." -ForegroundColor Green
Write-Host "Model directory: $modelRoot"

if ($installSherpa) {
    Write-Host ""
    Write-Host "The 'Sherpa-ONNX Zipformer' option in the app is now usable." -ForegroundColor Green
    Write-Host "Note: this model is bilingual Chinese + English only. Thai speech recognition" -ForegroundColor Yellow
    Write-Host "is not supported by it -- keep the ASR engine on Whisper if you need Thai." -ForegroundColor Yellow
} else {
    Write-Host ""
    Write-Host "The 'Sherpa-ONNX Zipformer' ASR option is NOT installed and will fail if selected." -ForegroundColor Yellow
    Write-Host "To install it (Python package + about 190 MB of model files), rerun:" -ForegroundColor Yellow
    Write-Host '  powershell -ExecutionPolicy Bypass -File .\tools\setup_local_models.ps1 -Sherpa' -ForegroundColor Yellow
}
