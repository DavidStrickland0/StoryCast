$ErrorActionPreference = 'Stop'
$library = 'C:\Users\User\Documents\StoryCast\samples'
$report = Get-Content -LiteralPath (Join-Path $library 'voice-verification.json') -Raw | ConvertFrom-Json
$template = Get-Content -LiteralPath (Join-Path $library 'Female-001\voice.json') -Raw | ConvertFrom-Json
$pending = foreach ($directory in Get-ChildItem -LiteralPath $library -Directory) {
    $target = Join-Path $directory.FullName 'voice.json'
    if (Test-Path -LiteralPath $target) { continue }
    $entry = @($report.voices | Where-Object directory -eq $directory.Name)
    if ($entry.Count -ne 1) { throw "Missing unique verification entry for $($directory.Name)" }
    $presentation = if ($directory.Name.StartsWith('Female-')) { 'female' } elseif ($directory.Name.StartsWith('Male-')) { 'male' } else { '' }
    $manifest = [ordered]@{
        id = $entry[0].voiceId
        sample = 'sample.wav'
        language = $entry[0].language
        accent = ''
        apparentAge = ''
        presentation = $presentation
        qualities = @()
        suitableRoles = @()
        narratorSuitable = $false
        sampleText = $template.sampleText
    }
    [pscustomobject]@{ Path = $target; Json = ($manifest | ConvertTo-Json -Depth 5) }
}
foreach ($item in $pending) {
    $stream = [System.IO.File]::Open($item.Path, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write)
    try {
        $bytes = [System.Text.UTF8Encoding]::new($false).GetBytes($item.Json + [Environment]::NewLine)
        $stream.Write($bytes, 0, $bytes.Length)
    } finally { $stream.Dispose() }
}
"Created $(@($pending).Count) missing manifests."
$missingSamples = 0
foreach ($directory in Get-ChildItem -LiteralPath $library -Directory) {
    $manifest = Get-Content -LiteralPath (Join-Path $directory.FullName 'voice.json') -Raw | ConvertFrom-Json
    if (!(Test-Path -LiteralPath (Join-Path $directory.FullName $manifest.sample))) { $missingSamples++ }
}
"Audio samples still missing: $missingSamples"
