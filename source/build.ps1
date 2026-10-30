param([string]$OutputPath = (Join-Path (Split-Path $PSScriptRoot -Parent) 'build\CodexUsageFloat.exe'))
$ErrorActionPreference = 'Stop'
$OutputPath = [System.IO.Path]::GetFullPath($OutputPath)
[System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($OutputPath)) | Out-Null
$compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compilerPath)) { throw '需要 Windows 自带的 .NET Framework 4.x。' }
$compileArgs = @(
    '/nologo', '/target:winexe', '/platform:x64', '/optimize+', '/warnaserror+', '/codepage:65001', '/utf8output',
    '/reference:System.dll', '/reference:System.Core.dll', '/reference:System.Drawing.dll',
    '/reference:System.Windows.Forms.dll', '/reference:System.Web.Extensions.dll',
    ('/win32manifest:' + (Join-Path $PSScriptRoot 'app.manifest')),
    ('/out:' + $OutputPath)
)
$compileArgs += @(Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.cs' -File | Sort-Object Name | ForEach-Object { $_.FullName })
& $compilerPath @compileArgs
if ($LASTEXITCODE -ne 0) { throw ('编译失败，退出码：' + $LASTEXITCODE) }
Get-Item -LiteralPath $OutputPath | Select-Object FullName,Length
