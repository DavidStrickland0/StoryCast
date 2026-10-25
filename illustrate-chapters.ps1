param([Parameter(Mandatory)][string]$Book, [string]$Chapters, [switch]$Detach)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $Book).Path
$tools = Join-Path $PSScriptRoot 'tools'
$arguments = @('run', '--mount', "type=bind,source=$root,target=/books", '--mount', "type=bind,source=$tools,target=/app/tools,readonly", '--entrypoint', 'python')
if ($Detach) { $arguments += '--detach' } else { $arguments += '--rm' }
$arguments += @('storycast-worker:dev', '/app/tools/chapter_art.py', '/books', '--wait-for-model', '--ollama-url', 'http://host.docker.internal:11434')
if ($Chapters) { $arguments += @('--chapters', $Chapters) }
& docker @arguments
if ($LASTEXITCODE) { throw 'Chapter illustration job failed; saved audio and artwork progress are preserved.' }
