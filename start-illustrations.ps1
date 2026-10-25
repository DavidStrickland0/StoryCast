param([switch]$Build, [string[]]$Books = @())
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    if ($Build) {
        docker build -f Dockerfile.illustrations -t storycast-illustrations:dev .
        if ($LASTEXITCODE) { throw 'Illustration renderer build failed.' }
    }
    docker inspect storycast-illustrations *> $null
    if ($LASTEXITCODE -eq 0) {
        if ($Build) {
            docker stop storycast-illustrations | Out-Null
            docker rm storycast-illustrations | Out-Null
            docker run --detach --name storycast-illustrations --restart unless-stopped --gpus all -p 127.0.0.1:7862:7862 --mount type=volume,source=storycast-illustration-models,target=/models storycast-illustrations:dev
        } else { docker start storycast-illustrations }
    } else {
        docker run --detach --name storycast-illustrations --restart unless-stopped --gpus all -p 127.0.0.1:7862:7862 --mount type=volume,source=storycast-illustration-models,target=/models storycast-illustrations:dev
    }
    if ($LASTEXITCODE) { throw 'Illustration renderer did not start.' }
    foreach ($book in $Books) {
        $root = (Resolve-Path -LiteralPath $book).Path
        $config = Join-Path $root 'production\illustrations.json'
        if (-not (Test-Path -LiteralPath $config)) {
            New-Item -ItemType Directory -Path (Split-Path $config) -Force | Out-Null
            @{ enabled = $true; plannerModel = 'qwen3.5:4b'; rendererUrl = 'http://host.docker.internal:7862'; size = 768 } |
                ConvertTo-Json | Set-Content -LiteralPath $config -Encoding utf8
        }
        Write-Host "Chapter illustrations configured: $root"
    }
    Write-Host 'Renderer: http://localhost:7862/health'
    Write-Host 'Progress: docker logs --follow storycast-illustrations'
} finally { Pop-Location }
