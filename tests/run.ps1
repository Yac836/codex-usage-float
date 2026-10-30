param(
    [string]$OutputDirectory = (Join-Path $env:TEMP ('codex-usage-float-tests-' + [Guid]::NewGuid().ToString('N'))),
    [switch]$Desktop
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$binaryDirectory = Join-Path $OutputDirectory 'bin'
New-Item -ItemType Directory -Path $binaryDirectory -Force | Out-Null
$application = Join-Path $binaryDirectory 'CodexUsageFloat.exe'
& (Join-Path $projectRoot 'source\build.ps1') -OutputPath $application
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$common = @('/nologo', '/target:exe', '/platform:x64', '/warnaserror+', '/codepage:65001', '/utf8output', '/reference:System.dll', '/reference:System.Core.dll', '/reference:System.Drawing.dll', '/reference:System.Windows.Forms.dll', '/reference:System.Web.Extensions.dll')
$fake = Join-Path $binaryDirectory 'FakeCodex.exe'
& $compiler @common ('/out:' + $fake) (Join-Path $PSScriptRoot 'FakeCodex.cs')
if ($LASTEXITCODE -ne 0) { throw 'Mock server compilation failed.' }
$testExe = Join-Path $binaryDirectory 'CodexUsageFloat.Tests.exe'
$testSources = @(Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.cs' -File | Where-Object { $_.Name -ne 'FakeCodex.cs' } | Sort-Object Name | ForEach-Object { $_.FullName })
& $compiler @common ('/reference:' + $application) ('/win32manifest:' + (Join-Path $projectRoot 'source\app.manifest')) ('/out:' + $testExe) @testSources
if ($LASTEXITCODE -ne 0) { throw 'Regression suite compilation failed.' }
$arguments = @(('"' + $OutputDirectory + '"'), ('"' + $fake + '"'))
if ($Desktop) { $arguments += '--desktop' }
$standardOutput = Join-Path $OutputDirectory 'stdout.txt'
$standardError = Join-Path $OutputDirectory 'stderr.txt'
$process = Start-Process -FilePath $testExe -ArgumentList $arguments -WindowStyle Hidden -PassThru -RedirectStandardOutput $standardOutput -RedirectStandardError $standardError
# Windows PowerShell 5.1 otherwise may release the original process handle
# and expose a null ExitCode after WaitForExit(int), even on a successful run.
$null = $process.Handle
if (-not $process.WaitForExit(180000)) {
    Stop-Process -Id $process.Id -ErrorAction SilentlyContinue
    throw 'Regression suite exceeded 3 minutes. Owned mock helpers will be terminated by their job objects.'
}
$process.WaitForExit()
Get-Content -LiteralPath $standardOutput -Encoding UTF8
if ((Get-Item -LiteralPath $standardError).Length -gt 0) { Get-Content -LiteralPath $standardError -Encoding UTF8 }
$reportPath = Join-Path $OutputDirectory 'results.json'
if (-not (Test-Path -LiteralPath $reportPath)) { throw 'Regression process did not produce a report.' }
$report = Get-Content -LiteralPath $reportPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($null -eq $process.ExitCode -or $process.ExitCode -ne 0 -or -not $report.success -or $report.failed -ne 0) { throw ('Regression failed. Report: ' + $reportPath) }
Write-Output ('Report: ' + (Join-Path $OutputDirectory 'results.json'))
