param([string]$MakeExe = 'make.exe')
$ErrorActionPreference = 'Stop'
$makeBin = Split-Path -Parent (Get-Command $MakeExe -CommandType Application -ErrorAction Stop).Source
$trial = Join-Path $PSScriptRoot '.build'
$source = Join-Path $trial 'ffmpeg-9.0.2'
# In-tree compilation keeps __FILE__ and diagnostic strings relative. The
# extracted tree is disposable; the upstream source archive stays unchanged.
$build = $source
$zlib = Join-Path $trial 'zlib'
$gitUnix = 'C:\Program Files\Git\usr\bin'
$vswhere = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe'
$vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
$msvc = Get-ChildItem -LiteralPath "$vs\VC\Tools\MSVC" -Directory | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$kits = 'C:\Program Files (x86)\Windows Kits\10'
$sdk = Get-ChildItem -LiteralPath "$kits\Include" -Directory | Where-Object { Test-Path "$($_.FullName)\um\Windows.h" } | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$env:INCLUDE = "$zlib;$($msvc.FullName)\include;$($sdk.FullName)\ucrt;$($sdk.FullName)\shared;$($sdk.FullName)\um;$($sdk.FullName)\winrt"
$env:LIB = "$zlib;$($msvc.FullName)\lib\x64;$kits\Lib\$($sdk.Name)\ucrt\x64;$kits\Lib\$($sdk.Name)\um\x64"
$env:PATH = "$($msvc.FullName)\bin\Hostx64\x64;$kits\bin\$($sdk.Name)\x64;$makeBin;$gitUnix;$env:PATH"
$env:TEMP = Join-Path $trial 'temp'
$env:TMP = $env:TEMP
# Header diagnostics can still use absolute paths with in-tree MSVC builds.
# Use CL, not configure's --extra-cflags: configure arguments are embedded in
# the DLL and would otherwise expose the original path in the mapping itself.
$env:CL = ($env:CL + ' /experimental:deterministic "/pathmap:' + $PSScriptRoot + '=."').Trim()
New-Item -ItemType Directory -Force -Path $build,$zlib,$env:TEMP | Out-Null

$archive = Join-Path $PSScriptRoot 'ffmpeg-9.0.2.tar.xz'
if ((Get-FileHash -LiteralPath $archive).Hash -ne '8C3850283EB25FA026482078A04051E0BE17347B09EF81A0849BEC15A96E002E') {
    throw "Source archive checksum mismatch: $archive"
}
if (!(Test-Path -LiteralPath "$source/configure")) {
    & "$env:SystemRoot\System32\tar.exe" -xf $archive -C $trial
    if ($LASTEXITCODE) { throw 'FFmpeg extraction failed' }
}
# Link only the decompression half of zlib into avformat. No separate zlib DLL.
$zlibSource = Join-Path $PSScriptRoot 'zlib'
$units = @('adler32','crc32','inflate','inffast','inftrees','uncompr','zutil')
$env:INCLUDE = "$zlibSource;$env:INCLUDE"
Push-Location $zlib
try {
    & cl.exe /nologo /c /O1 /Gy /MT @($units | ForEach-Object { "$zlibSource\$_.c" }) *> "$trial/zlib-build.log"
    if ($LASTEXITCODE) { throw 'zlib compile failed' }
    & lib.exe /nologo /OUT:zlib.lib @($units | ForEach-Object { "$_.obj" }) *>> "$trial/zlib-build.log"
    if ($LASTEXITCODE) { throw 'zlib archive failed' }
} finally { Pop-Location }

Push-Location $build
try {
    & "$gitUnix/bash.exe" --noprofile --norc -c 'make --version && sh ./configure --toolchain=msvc --arch=x86_64 --enable-shared --disable-static --disable-everything --disable-autodetect --disable-programs --disable-doc --disable-network --disable-avdevice --disable-avfilter --disable-swscale --disable-swresample --disable-asm --disable-debug --disable-stripping --enable-small --enable-demuxer=matroska --enable-protocol=file --enable-zlib --extra-cflags=-MT' *> "$trial/configure.log"
    if ($LASTEXITCODE) { Get-Content "$trial/configure.log" -Tail 20; throw 'FFmpeg configure failed' }
    & "$gitUnix/bash.exe" --noprofile --norc -c 'make clean && make -j8' *> "$trial/ffmpeg-build.log"
    if ($LASTEXITCODE) { Get-Content "$trial/ffmpeg-build.log" -Tail 30; throw 'FFmpeg build failed' }
} finally { Pop-Location }

$dist = Join-Path $trial 'runtime'
New-Item -ItemType Directory -Force -Path $dist | Out-Null
Copy-Item -LiteralPath "$source/COPYING.LGPLv2.1" -Destination "$dist/FFmpeg-LGPL-2.1.txt"
Copy-Item -LiteralPath "$zlibSource/LICENSE" -Destination "$dist/zlib-LICENSE.txt"
$imports = @('avformat','avcodec','avutil') | ForEach-Object { "$build/lib$_/$_.lib" }
foreach ($part in @('avformat','avcodec','avutil')) {
    Get-ChildItem -LiteralPath "$build/lib$part" -Filter "$part-*.dll" -File |
        Copy-Item -Destination $dist
}
Push-Location $build
try {
    & cl.exe /nologo /LD /O1 /MT /utf-8 "/I$source" "/I$build" "$PSScriptRoot/reader.c" @imports "/Fe$dist/NicoBE.Mkv.dll" /link "/IMPLIB:$build/NicoBE.Mkv.lib" *> "$trial/reader-build.log"
    if ($LASTEXITCODE) { Get-Content "$trial/reader-build.log" -Tail 25; throw 'Reader build failed' }
    Get-ChildItem -LiteralPath $dist -Filter '*.dll' | ForEach-Object {
        "--- $($_.Name) ($($_.Length) bytes) ---"
        & dumpbin.exe /nologo /dependents $_.FullName
    } | Set-Content -Encoding UTF8 "$trial/dependencies.txt"
} finally { Pop-Location }

# Refuse to publish DLLs that retain an absolute build path. Checking the PE
# bytes also catches diagnostic strings that are not part of debug symbols.
foreach ($dll in Get-ChildItem -LiteralPath $dist -Filter '*.dll' -File) {
    $bytes = [IO.File]::ReadAllBytes($dll.FullName)
    foreach ($text in @([Text.Encoding]::ASCII.GetString($bytes), [Text.Encoding]::Unicode.GetString($bytes))) {
        if ([regex]::IsMatch($text, '(?<![A-Za-z])[A-Za-z]:[\\/]') -or
            $text.IndexOf($PSScriptRoot, [StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $text.IndexOf($PSScriptRoot.Replace('\','/'), [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            throw "Absolute build path remains in $($dll.Name); runtime was not updated."
        }
    }
}
$runtime = Join-Path $PSScriptRoot 'runtime'
New-Item -ItemType Directory -Force -Path $runtime | Out-Null
Get-ChildItem -LiteralPath $dist -File | Copy-Item -Destination $runtime
Write-Output 'MKV DLLs rebuilt and checked: no absolute build paths.'
