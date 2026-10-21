# 编译 TouchToggle.exe，再用 Inno Setup 打包成安装程序，输出到 dist\
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot

& (Join-Path $root 'build.ps1')
if ($LASTEXITCODE -ne 0) { throw '编译 TouchToggle.exe 失败' }

$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw '未找到 Inno Setup 6，请先安装：winget install JRSoftware.InnoSetup' }

& $iscc /Q (Join-Path $PSScriptRoot 'TouchToggle.iss')
if ($LASTEXITCODE -ne 0) { throw '打包安装程序失败' }
Get-ChildItem (Join-Path $root 'dist') -Filter *.exe | Select-Object Name, Length, LastWriteTime
