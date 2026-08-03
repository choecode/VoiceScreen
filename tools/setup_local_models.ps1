$ErrorActionPreference = "Stop"

Write-Host "VoiceScreen local model setup" -ForegroundColor Cyan
Write-Host "This downloads Whisper small and two OPUS-MT translation models once."

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
    "huggingface-hub>=0.34,<1"
if ($LASTEXITCODE -ne 0) { throw "Failed to install runtime packages." }

$env:HF_HUB_OFFLINE = "0"
$env:HF_HUB_DISABLE_XET = "1"
$modelRoot = Join-Path $env:LOCALAPPDATA "VoiceScreen\Models"
New-Item -ItemType Directory -Force -Path $modelRoot | Out-Null

$zhEnReady = Test-Path (Join-Path $modelRoot "opus-mt-zh-en-ct2-int8\model.bin")
$enZhReady = Test-Path (Join-Path $modelRoot "opus-mt-en-zh-ct2-int8\model.bin")
if (-not ($zhEnReady -and $enZhReady)) {
    # Torch is only used by the one-time official Transformers -> CTranslate2 conversion.
    # 2.6+ is required because older torch.load versions have a known security issue.
    python -m pip install --index-url https://download.pytorch.org/whl/cpu "torch>=2.6,<3"
    if ($LASTEXITCODE -ne 0) { throw "Failed to install the safe CPU build of PyTorch." }
}

Write-Host "Downloading Whisper small..." -ForegroundColor Cyan
$env:HF_HUB_OFFLINE = "1"
python -c "from faster_whisper import WhisperModel; WhisperModel('small', device='cpu', compute_type='int8', local_files_only=True); print('Whisper small already cached')"
if ($LASTEXITCODE -ne 0) {
    $env:HF_HUB_OFFLINE = "0"
    python -c "from faster_whisper import WhisperModel; WhisperModel('small', device='cpu', compute_type='int8'); print('Whisper small ready')"
    if ($LASTEXITCODE -ne 0) { throw "Failed to download Whisper small. Check the network and rerun this script to resume." }
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
    hf download $Repository --local-dir $source --include `
        config.json generation_config.json pytorch_model.bin source.spm target.spm vocab.json tokenizer_config.json
    if ($LASTEXITCODE -ne 0) { throw "Failed to download $Repository." }

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

$env:HF_HUB_OFFLINE = "1"
Write-Host "All VoiceScreen local models are ready." -ForegroundColor Green
Write-Host "Model directory: $modelRoot"
