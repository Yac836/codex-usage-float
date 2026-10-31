param([string]$ProjectRoot = (Split-Path $PSScriptRoot -Parent))
$ErrorActionPreference = 'Stop'
$ProjectRoot = [System.IO.Path]::GetFullPath($ProjectRoot)
$manifest = Join-Path $ProjectRoot 'tools\public-files.txt'
$publicFiles = @(Get-Content -LiteralPath $manifest -Encoding UTF8 | Where-Object { $_.Trim() -and -not $_.StartsWith('#') } | ForEach-Object { $_.Trim() })
if (@($publicFiles | Sort-Object -Unique).Count -ne $publicFiles.Count) { throw 'Duplicate entries in public-files.txt.' }
$forbidden = @(
    '(?i)[A-Z]:[\\/](?:Users|个人)[\\/]',
    'sk-[A-Za-z0-9_-]{24,}',
    '-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----',
    '(?i)"(?:access_token|refresh_token|api_key)"\s*:\s*"[^"\r\n]{12,}"'
)
foreach ($relative in $publicFiles) {
    if ($relative -match '(^[A-Za-z]:|^/|\\|(^|/)\.\.(/|$))') { throw "Unsafe public file path: $relative" }
    $path = Join-Path $ProjectRoot $relative
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing public file: $relative" }
    if ([System.IO.Path]::GetExtension($path) -eq '.png') {
        $bytes = [System.IO.File]::ReadAllBytes($path)
        if ($bytes.Length -lt 8 -or $bytes.Length -gt 2MB -or [BitConverter]::ToString($bytes, 0, 8) -ne '89-50-4E-47-0D-0A-1A-0A') { throw "Invalid or oversized preview PNG: $relative" }
        continue
    }
    $text = [System.IO.File]::ReadAllText($path, [System.Text.Encoding]::UTF8)
    foreach ($pattern in $forbidden) {
        if ($text -match $pattern) { throw "Potential private content in public file: $relative (inspect locally; content omitted)" }
    }
}
foreach ($directory in @('source', 'tests')) {
    Get-ChildItem -LiteralPath (Join-Path $ProjectRoot $directory) -Filter '*.cs' -File | ForEach-Object {
        $relative = $directory + '/' + $_.Name
        if ($publicFiles -notcontains $relative) { throw "Source missing from public manifest: $relative" }
    }
}
$workflow = [System.IO.File]::ReadAllText((Join-Path $ProjectRoot '.github\workflows\ci.yml'))
foreach ($match in [regex]::Matches($workflow, 'uses:\s+([^\s#]+)')) {
    if ($match.Groups[1].Value -notmatch '^actions/(checkout|upload-artifact)@[a-f0-9]{40}$') { throw 'Workflow action must be an approved action pinned to a full commit SHA.' }
}
if ($workflow -match 'pull_request_target|write-all|contents:\s*write') { throw 'Unexpected privileged workflow configuration.' }
if ((Get-Content -LiteralPath (Join-Path $ProjectRoot 'LICENSE') -Encoding UTF8 -TotalCount 1) -ne 'MIT License') { throw 'Missing MIT license.' }
Write-Output ("Public source check passed: {0} files. This check complements manual review; it is not a complete secret detector." -f $publicFiles.Count)
