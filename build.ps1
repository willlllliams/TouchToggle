# 用 Windows 自带的 .NET Framework 编译器生成 TouchToggle.exe
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
Set-Location $PSScriptRoot
& $csc /nologo /target:winexe /codepage:65001 /optimize+ /win32manifest:app.manifest `
    /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Management.dll `
    /out:TouchToggle.exe TouchToggle.cs
