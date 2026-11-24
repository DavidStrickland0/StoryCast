#requires -Version 7.0
<#
Build candidates after current production ends:
  pwsh -File .\tools\Upgrade-AudioStack.ps1
Compare installed Ollama models on isolated copies of chapter 1:
  pwsh -File .\tools\Upgrade-AudioStack.ps1 -Action BenchmarkAnalysis
Compare Whisper serial/batched transcription using completed chapter audio:
  pwsh -File .\tools\Upgrade-AudioStack.ps1 -Action BenchmarkWhisper -AudioPath 'C:\path\chapter-001.mp3'
Compare actual VoiceDesign SDPA/FlashAttention generation:
  pwsh -File .\tools\Upgrade-AudioStack.ps1 -Action BenchmarkVoice
Continue production with candidates (explicit action; never starts during Build):
  pwsh -File .\tools\Upgrade-AudioStack.ps1 -Action Produce
Outputs, logs, dependency snapshots and benchmark JSON stay under build/audio-stack.
No existing image tags, source dependencies, casting or production settings are replaced.
#>
[CmdletBinding()]
param(
    [ValidateSet('Build','BenchmarkAnalysis','BenchmarkWhisper','BenchmarkVoice','Produce')]
    [string]$Action = 'Build',
    [string]$BookPath = 'C:\Repos\SRD\Judicator StoryCast',
    [string]$VoiceLibrary = 'C:\Repos\SRD\StoryCast\voices',
    [string[]]$Models = @('qwen3.5:9b','qwen3.8:27b'),
    [string]$ProductionModel = 'qwen3.8:27b',
    [switch]$OneChapter,
    [string]$AudioPath,
    [string]$FlashAttentionVersion = '2.8.3',
    [ValidateRange(1,8)][int]$CompileJobs = 2,
    [string]$WorkerImage = 'storycast-worker:optimized',
    [string]$VoiceImage = 'voiceforge-design:optimized'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$forge = Join-Path (Split-Path $root -Parent) 'VoiceForge'
$session = Join-Path $root ('build\audio-stack\' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
New-Item -ItemType Directory -Path $session -Force | Out-Null
$utf8 = [Text.UTF8Encoding]::new($false)
function Write-Text([string]$Path,[string]$Value) { [IO.File]::WriteAllText($Path,$Value,$utf8) }
function Invoke-Checked([string]$Exe,[string[]]$Arguments) {
    & $Exe @Arguments 2>&1 | Tee-Object -FilePath (Join-Path $session 'commands.log') -Append | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "$Exe failed (exit $LASTEXITCODE). See $session" }
}
function Assert-Idle {
    $running = @(& docker ps --format '{{.Image}} {{.Names}} {{.Command}}')
    if ($LASTEXITCODE -ne 0) { throw 'Docker Desktop is unavailable.' }
    if ($running | Where-Object { $_ -match 'storycast|voiceforge|produce_chapter|generate_candidate' }) {
        throw 'An audio container is still running. Wait for Chapter 1 to finish before continuing.'
    }
}
Start-Transcript -Path (Join-Path $session 'session.log') | Out-Null
try {
    Push-Location $root
    Assert-Idle
    if ($Action -eq 'Build') {
        # Generate a separate build context; original Dockerfiles stay untouched.
        $context = Join-Path $session 'worker'
        New-Item -ItemType Directory -Path $context | Out-Null
        Copy-Item -LiteralPath (Join-Path $root 'tools') -Destination $context -Recurse
        $requirements = Get-Content (Join-Path $root 'requirements.worker.txt') -Raw
        # Chatterbox pins Torch 2.6 in its package metadata. Keep its initial
        # dependency resolution intact, then apply the existing tested Torch
        # override. Resolving Chatterbox together with Torch 2.7 fails in pip.
        Write-Text (Join-Path $context 'requirements.worker.txt') $requirements
        $dockerfile = Get-Content (Join-Path $root 'Dockerfile.worker') -Raw
        $dockerfile = $dockerfile.Replace('12.6.3-runtime','12.8.1-runtime')
        Write-Text (Join-Path $context 'Dockerfile') $dockerfile
        Invoke-Checked docker @('build','--progress=plain','-t',$WorkerImage,$context)
        $voiceContext = Join-Path $session 'voice'
        New-Item -ItemType Directory -Path $voiceContext | Out-Null
        foreach ($name in @('app.py','generate_request.py','generate_candidate.py','model_loading.py')) {
            Copy-Item -LiteralPath (Join-Path $forge $name) -Destination $voiceContext
        }
        $appPath = Join-Path $voiceContext 'app.py'
        $app = Get-Content $appPath -Raw
        if (-not $app.Contains("attn_implementation='sdpa'")) { throw 'VoiceForge attention configuration changed; inspect app.py before upgrading.' }
        $app = $app.Replace("attn_implementation='sdpa'", "attn_implementation=os.environ.get('VOICEFORGE_ATTENTION', 'sdpa') if device == 'cuda' else 'sdpa'")
        Write-Text $appPath $app
        foreach ($file in Get-ChildItem -LiteralPath $forge -Filter '*.py') {
            if ($file.Name -ne 'app.py') { Copy-Item -LiteralPath $file.FullName -Destination $voiceContext }
        }
        $voiceDocker = @'
ARG WORKER_IMAGE=storycast-worker:optimized
FROM ${WORKER_IMAGE} AS worker
FROM nvidia/cuda:12.8.1-devel-ubuntu24.04 AS builder
RUN apt-get update && apt-get install -y --no-install-recommends python3.12 python3.12-venv python3-pip build-essential git && rm -rf /var/lib/apt/lists/*
COPY --from=worker /opt/storycast /opt/storycast
ENV PATH=/opt/storycast/bin:${PATH}
ARG FLASH_VERSION
ARG COMPILE_JOBS=2
RUN python -m pip install ninja packaging psutil wheel && MAX_JOBS=${COMPILE_JOBS} python -m pip wheel flash-attn==${FLASH_VERSION} --no-build-isolation --no-deps --wheel-dir /wheels
FROM worker
RUN apt-get update && apt-get install -y --no-install-recommends sox && rm -rf /var/lib/apt/lists/*
RUN python -m pip install qwen-tts==0.1.1 soundfile
COPY --from=builder /wheels /tmp/flash-wheels
RUN python -m pip install --no-deps /tmp/flash-wheels/*.whl && rm -rf /tmp/flash-wheels
COPY app.py generate_request.py generate_candidate.py model_loading.py /app/
ENV VOICEFORGE_ATTENTION=sdpa
ENTRYPOINT ["python", "/app/generate_request.py"]
'@
        Write-Text (Join-Path $voiceContext 'Dockerfile') $voiceDocker
        Invoke-Checked docker @('build','--progress=plain','--build-arg',"WORKER_IMAGE=$WorkerImage",'--build-arg',"FLASH_VERSION=$FlashAttentionVersion",'--build-arg',"COMPILE_JOBS=$CompileJobs",'-t',$VoiceImage,$voiceContext)
        # Check the kernel on the actual GPU, not only whether import succeeds.
        $probe = "import torch,flash_attn; from flash_attn import flash_attn_func; q=torch.randn(1,128,4,64,device='cuda',dtype=torch.bfloat16); y=flash_attn_func(q,q,q); torch.cuda.synchronize(); print(torch.__version__,flash_attn.__version__,torch.cuda.get_device_name(0),y.shape)"
        Invoke-Checked docker @('run','--rm','--gpus','all','--entrypoint','python',$VoiceImage,'-c',$probe)
        foreach ($image in @($WorkerImage,$VoiceImage)) {
            $name = $image.Replace(':','-').Replace('/','-')
            $freeze = & docker run --rm --entrypoint python $image -m pip freeze
            if ($LASTEXITCODE -ne 0) { throw "Unable to freeze $image" }
            Write-Text (Join-Path $session "$name.lock.txt") ($freeze -join "`n")
            $check = & docker run --rm --entrypoint python $image -m pip check 2>&1
            $checkExit = $LASTEXITCODE
            Write-Text (Join-Path $session "$name.pip-check.txt") ($check -join "`n")
            if ($checkExit -ne 0) { Write-Warning "Dependency metadata conflicts in $image; review $name.pip-check.txt before production." }
            $inspect = & docker image inspect $image
            Write-Text (Join-Path $session "$name.image.json") ($inspect -join "`n")
            # Exact local snapshot: usable without resolving dependencies again.
            $snapshot = $image.Split(':')[0] + ':snapshot-' + (Split-Path $session -Leaf)
            Invoke-Checked docker @('tag',$image,$snapshot)
        }
        Write-Host 'Candidate images built. Benchmark them before using -Action Produce.'
        Write-Text (Join-Path $root 'build/audio-stack/candidate.json') (@{voiceSource=$voiceContext;workerImage=$WorkerImage;voiceImage=$VoiceImage} | ConvertTo-Json)
        Write-Host 'Lock files document the resolved environment; retained build contexts permit review. Snapshot tags retain the exact local images.'
    }
    elseif ($Action -eq 'BenchmarkAnalysis') {
        Invoke-Checked dotnet @('build','src/StoryCast.Cli','-c','Release')
        $manifest = Get-Content (Join-Path $BookPath 'book.json') -Raw | ConvertFrom-Json
        $results = @()
        foreach ($model in $Models) {
            $testBook = Join-Path $session ('analysis-' + ($model -replace '[^a-zA-Z0-9.-]','_'))
            New-Item -ItemType Directory -Path $testBook | Out-Null
            Copy-Item (Join-Path $BookPath $manifest.chapters[0]) (Join-Path $testBook 'chapter-001.txt')
            Write-Text (Join-Path $testBook 'book.json') (@{schemaVersion=1;id='judicator-benchmark';title='Judicator benchmark';author=$manifest.author;language=$manifest.language;chapters=@('chapter-001.txt')} | ConvertTo-Json)
            foreach ($stage in @(@('characters','discover'),@('dialogue','attribute'))) {
                $timer = [Diagnostics.Stopwatch]::StartNew()
                & dotnet run --no-build -c Release --project src/StoryCast.Cli -- @stage $testBook --model $model --force
                $code = $LASTEXITCODE
                $timer.Stop()
                $results += [pscustomobject]@{model=$model;stage=($stage -join ' ');seconds=$timer.Elapsed.TotalSeconds;exitCode=$code;artifacts=$testBook}
                $results | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $session 'analysis-results.json')
                if ($code -ne 0) { break }
            }
        }
        Write-Host 'Compare production/scripts and characters.json in the isolated benchmark folders for speaker accuracy. Casting was not changed or benchmarked; it depends on the verified voice library.'
    }
    elseif ($Action -eq 'BenchmarkWhisper') {
        if (-not $AudioPath -or -not (Test-Path -LiteralPath $AudioPath -PathType Leaf)) { throw 'Specify -AudioPath pointing to completed chapter audio.' }
        Copy-Item -LiteralPath $AudioPath -Destination (Join-Path $session 'sample.audio')
        $python = @'
import json,time,torch
from pathlib import Path
from faster_whisper import WhisperModel,BatchedInferencePipeline
root=Path('/bench'); results=[]
for mode in ('serial','batch2','batch4'):
    model=WhisperModel('small.en',device='cuda',compute_type='float16')
    engine=model if mode=='serial' else BatchedInferencePipeline(model=model)
    options={} if mode=='serial' else {'batch_size':int(mode[-1])}
    started=time.perf_counter()
    segments,info=engine.transcribe('/bench/sample.audio',beam_size=5,vad_filter=True,**options)
    rows=[{'start':s.start,'end':s.end,'text':s.text} for s in segments]
    elapsed=time.perf_counter()-started
    (root/(mode+'.json')).write_text(json.dumps(rows,indent=2))
    results.append(dict(mode=mode,seconds=elapsed,audioSeconds=info.duration,realTimeFactor=elapsed/info.duration))
    (root/'whisper-results.json').write_text(json.dumps(results,indent=2))
    del engine,model
    import gc; gc.collect(); torch.cuda.empty_cache()
print(json.dumps(results,indent=2))
'@
        Write-Text (Join-Path $session 'benchmark.py') $python
        Invoke-Checked docker @('run','--rm','--gpus','all','--mount',"type=bind,source=$session,target=/bench",'--mount','type=volume,source=storycast-models,target=/models','--entrypoint','python',$WorkerImage,'/bench/benchmark.py')
        Write-Host 'Batching is evaluated only. Compare transcripts before changing production verification.'
    }
    elseif ($Action -eq 'BenchmarkVoice') {
        $python = @'
import os,time,json,gc,sys
sys.path.insert(0,'/app')
from pathlib import Path
import torch,app
root=Path('/bench'); results=[]
app.PREVIEWS=root/'voices'
for backend in ('sdpa','flash_attention_2'):
    os.environ['VOICEFORGE_ATTENTION']=backend
    app.model=None; gc.collect(); torch.cuda.empty_cache()
    for attempt in range(2):
        torch.cuda.reset_peak_memory_stats(); started=time.perf_counter()
        audio,identity,_=app.design('Benchmark narrator','A clear, steady adult narrator.','unspecified','','','clear, steady','narrator',True,1234)
        torch.cuda.synchronize()
        results.append(dict(backend=backend,attempt=attempt,seconds=time.perf_counter()-started,peakAllocatedGB=torch.cuda.max_memory_allocated()/1024**3,audio=audio))
        (root/'voice-results.json').write_text(json.dumps(results,indent=2))
print(json.dumps(results,indent=2))
'@
        Write-Text (Join-Path $session 'benchmark.py') $python
        Invoke-Checked docker @('run','--rm','--gpus','all','--mount',"type=bind,source=$session,target=/bench",'--mount','type=volume,source=voiceforge-models,target=/models','--entrypoint','python',$VoiceImage,'/bench/benchmark.py')
    }
    elseif ($Action -eq 'Produce') {
        $candidate = Get-Content (Join-Path $root 'build/audio-stack/candidate.json') -Raw | ConvertFrom-Json
        if (-not (Test-Path -LiteralPath $candidate.voiceSource)) { throw 'Candidate VoiceForge source is missing; run Build first.' }
        # Chapter workers execute packaged tools, unlike VoiceForge's mounted
        # source. Refresh only the tools layer so fixes reach resumed runs.
        $patchContext = Join-Path $session 'worker-tools'
        New-Item -ItemType Directory -Path $patchContext | Out-Null
        Copy-Item -LiteralPath (Join-Path $root 'tools') -Destination $patchContext -Recurse
        Write-Text (Join-Path $patchContext 'Dockerfile') "ARG BASE_IMAGE=storycast-worker:optimized`nFROM `${BASE_IMAGE}`nCOPY tools /app/tools`n"
        $patchedWorker = ($WorkerImage -split ':')[0] + ':production-tools'
        Invoke-Checked docker @('build','--progress=plain','--build-arg',"BASE_IMAGE=$WorkerImage",'-t',$patchedWorker,$patchContext)
        $normalizationProbe = "from verify_voice_library import normalize_words; assert normalize_words('Queue Eight') == normalize_words('Q8'); assert normalize_words('Queue Eight') != normalize_words('Q9'); print('Worker verification normalization passed')"
        Invoke-Checked docker @('run','--rm','--entrypoint','python',$patchedWorker,'-c',$normalizationProbe)
        $productionCommand = if ($OneChapter) { 'next' } else { 'run' }
        Invoke-Checked dotnet @('run','--project','src/StoryCast.Cli','--','produce',$productionCommand,$BookPath,'--model',$ProductionModel,'--library',$VoiceLibrary,'--voiceforge',$candidate.voiceSource,'--voiceforge-image',$VoiceImage,'--worker-image',$patchedWorker,'--audio-preparation-model',$ProductionModel,'--speech-repair-model',$ProductionModel,'--review-dialogue','--allow-review','--no-ambience')
    }
}
finally {
    Pop-Location
    Stop-Transcript | Out-Null
    Write-Host "Results: $session"
}
