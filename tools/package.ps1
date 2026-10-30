param(
    [string]$OutputDirectory = (Join-Path (Split-Path $PSScriptRoot -Parent) 'dist'),
    [string]$ExecutablePath
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
& (Join-Path $PSScriptRoot 'check-source.ps1') -ProjectRoot $projectRoot
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
if (-not $ExecutablePath) {
    $ExecutablePath = Join-Path $projectRoot 'build\CodexUsageFloat.exe'
    & (Join-Path $projectRoot 'source\build.ps1') -OutputPath $ExecutablePath
}
$ExecutablePath = [System.IO.Path]::GetFullPath($ExecutablePath)
if (-not (Test-Path -LiteralPath $ExecutablePath -PathType Leaf)) { throw 'The executable to package does not exist.' }
$stagingRoot = Join-Path $env:TEMP ('codex-usage-package-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stagingRoot | Out-Null
try {
    $binaryStage = Join-Path $stagingRoot 'portable'
    New-Item -ItemType Directory -Path $binaryStage | Out-Null
    Copy-Item -LiteralPath $ExecutablePath -Destination (Join-Path $binaryStage 'CodexUsageFloat.exe')
    foreach ($name in @('LICENSE', 'README.md')) { Copy-Item -LiteralPath (Join-Path $projectRoot $name) -Destination (Join-Path $binaryStage $name) }
    $sourceStage = Join-Path $stagingRoot 'source'
    New-Item -ItemType Directory -Path $sourceStage | Out-Null
    $publicFiles = @(Get-Content -LiteralPath (Join-Path $PSScriptRoot 'public-files.txt') -Encoding UTF8 | Where-Object { $_.Trim() -and -not $_.StartsWith('#') } | ForEach-Object { $_.Trim() })
    foreach ($relative in $publicFiles) {
        $destination = Join-Path $sourceStage $relative
        New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $projectRoot $relative) -Destination $destination
    }
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    foreach ($package in @(@{ Directory = $binaryStage; Name = 'CodexUsageFloat-windows-x64.zip' }, @{ Directory = $sourceStage; Name = 'CodexUsageFloat-source.zip' })) {
        $target = Join-Path $OutputDirectory $package.Name
        if (Test-Path -LiteralPath $target) { throw "Package already exists; choose a new output directory: $target" }
        $creating = [System.IO.Compression.ZipFile]::Open($target, [System.IO.Compression.ZipArchiveMode]::Create)
        $entryNames = if ($package.Name -eq 'CodexUsageFloat-source.zip') { $publicFiles } else { @('CodexUsageFloat.exe', 'LICENSE', 'README.md') }
        try {
            foreach ($entryName in $entryNames) {
                [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($creating, (Join-Path $package.Directory $entryName), $entryName, [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
            }
        } finally { $creating.Dispose() }
        $archive = [System.IO.Compression.ZipFile]::OpenRead($target)
        try {
            $actualNames = @($archive.Entries | ForEach-Object { $_.FullName })
            if ($archive.Entries.Count -ne $entryNames.Count -or @(Compare-Object -ReferenceObject $entryNames -DifferenceObject $actualNames).Count -ne 0) { throw "Unexpected package contents: $target" }
        } finally { $archive.Dispose() }
        Get-Item -LiteralPath $target | Select-Object FullName, Length
    }
} finally {
    $resolvedStage = [System.IO.Path]::GetFullPath($stagingRoot)
    $resolvedTemp = [System.IO.Path]::GetFullPath($env:TEMP).TrimEnd('\') + '\'
    if (-not $resolvedStage.StartsWith($resolvedTemp, [StringComparison]::OrdinalIgnoreCase) -or -not ((Split-Path $resolvedStage -Leaf) -like 'codex-usage-package-*')) { throw 'Refusing to remove unexpected staging path.' }
    Remove-Item -LiteralPath $resolvedStage -Recurse -Force
}
