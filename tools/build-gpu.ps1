param([string]$OutputDirectory='')
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'console-encoding.ps1')
$root=Split-Path -Parent $PSScriptRoot
if(!$OutputDirectory){$OutputDirectory=Join-Path $root 'app'}
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
if(!$OutputDirectory.StartsWith($root+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'GPU output must stay inside nicobe.'}
$vswhere='C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe'
$vs=& $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw 'Visual C++ x64 build tools are required.' }
$msvc=Get-ChildItem -LiteralPath (Join-Path $vs 'VC\Tools\MSVC') -Directory | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$kits='C:\Program Files (x86)\Windows Kits\10'
$sdk=Get-ChildItem -LiteralPath (Join-Path $kits 'Include') -Directory | Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'um\dcomp.h') } | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$env:INCLUDE=@((Join-Path $msvc.FullName 'include'),(Join-Path $sdk.FullName 'ucrt'),(Join-Path $sdk.FullName 'shared'),(Join-Path $sdk.FullName 'um'),(Join-Path $sdk.FullName 'winrt')) -join ';'
$env:LIB=@((Join-Path $msvc.FullName 'lib\x64'),(Join-Path $kits "Lib\$($sdk.Name)\ucrt\x64"),(Join-Path $kits "Lib\$($sdk.Name)\um\x64")) -join ';'
$env:TEMP=Join-Path $root 'temp';$env:TMP=$env:TEMP
$output=Join-Path $root 'temp\test-results\gpu-build'
New-Item -ItemType Directory -Force -Path $output,$OutputDirectory,$env:TEMP | Out-Null
$compiler=Join-Path $msvc.FullName 'bin\Hostx64\x64\cl.exe'
# MSVC diagnostics use the Windows ANSI code page, independently of dotnet's UTF-8.
$consoleEncoding=[Console]::OutputEncoding
try {
    $ansiCodePage=[int](Get-ItemPropertyValue -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Control\Nls\CodePage' -Name ACP)
    [Console]::OutputEncoding=[Text.Encoding]::GetEncoding($ansiCodePage)
    & $compiler /nologo /LD /MT /std:c++17 /EHsc /O2 /W4 /utf-8 /DUNICODE /D_UNICODE /D_WIN32_WINNT=0x0A00 "/Fo$output\renderer.obj" "/Fd$output\renderer.pdb" "/Fe$OutputDirectory\NicoBE.Gpu.dll" (Join-Path $root 'src\NicoBE.Gpu\renderer.cpp') /link "/IMPLIB:$output\NicoBE.Gpu.lib" d3d11.lib dxgi.lib d2d1.lib dwrite.lib dcomp.lib windowscodecs.lib ole32.lib user32.lib *> (Join-Path $root 'temp\test-results\gpu-build.log')
} finally {
    [Console]::OutputEncoding=$consoleEncoding
}
if($LASTEXITCODE -ne 0) { Get-Content -LiteralPath (Join-Path $root 'temp\test-results\gpu-build.log') -Encoding UTF8 -Tail 45;throw 'GPU build failed.' }
Get-Content -LiteralPath (Join-Path $root 'temp\test-results\gpu-build.log') -Encoding UTF8 -Tail 10
