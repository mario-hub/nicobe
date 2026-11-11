param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$output=[IO.Path]::GetFullPath($OutputDirectory)
$tempRoot=[IO.Path]::GetFullPath((Join-Path $root 'temp\test-results'))+[IO.Path]::DirectorySeparatorChar
if(!$output.StartsWith($tempRoot,[StringComparison]::OrdinalIgnoreCase)){throw 'Source preparation must stay under temp/test-results.'}
if(Test-Path -LiteralPath $output){throw 'Source preparation requires a new output directory.'}
$ancestor=Split-Path -Parent $output
while($ancestor -and $ancestor.Length -ge $root.Length){
    if((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Source preparation path must not contain links.'}
    $ancestor=Split-Path -Parent $ancestor
}
$source=Join-Path $root 'src\NicoBE.Mkv'
$archive=Join-Path $source 'ffmpeg-9.0.2.tar.xz'
$upstreamHash='8C3850283EB25FA026482078A04051E0BE17347B09EF81A0849BEC15A96E002E'
if((Get-FileHash -LiteralPath $archive).Hash -ne $upstreamHash){throw 'FFmpeg upstream archive checksum mismatch.'}
$work=$output+'-work'
if(Test-Path -LiteralPath $work){throw 'Source work directory already exists.'}
New-Item -ItemType Directory -Path $output,$work | Out-Null
try {
    $tar=Join-Path $env:SystemRoot 'System32\tar.exe'
    & $tar -xf $archive -C $work
    if($LASTEXITCODE){throw 'FFmpeg source extraction failed.'}
    $tree=Join-Path $work 'ffmpeg-9.0.2'
    if(Get-ChildItem -LiteralPath $tree -Recurse -Force | Where-Object {$_.Attributes -band [IO.FileAttributes]::ReparsePoint}){throw 'Unexpected link in source tree.'}
    $kept=New-Object 'System.Collections.Generic.List[string]'
    $removed=New-Object 'System.Collections.Generic.List[string]'
    $required=New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
    foreach($line in Get-Content -LiteralPath (Join-Path $source 'ffmpeg-required-sources.txt')){
        if($line -and !$line.StartsWith('#')){
            if($line.Contains('..') -or $line -match '[:\\]' -or !(Test-Path -LiteralPath (Join-Path $tree $line))){throw "Invalid FFmpeg dependency: $line"}
            $null=$required.Add($line)
        }
    }
    foreach($file in Get-ChildItem -LiteralPath $tree -File -Recurse -Force){
        $relative=$file.FullName.Substring($tree.Length+1).Replace('\','/')
        # Retain compiled/included sources and configure registries, plus all
        # headers, build rules and licenses. No source file is edited.
        $unusedCode=$relative -match '^lib(avcodec|avfilter|avformat|avdevice|swscale|swresample)/.*\.(c|cpp|S|asm|cl|cu|glsl)$' -and !$required.Contains($relative)
        if($unusedCode -or $relative -match '^tests/(ref|filtergraphs|maps|streamgroups)/' -or
           $relative -match '^doc/.*\.(texi|css|html|svg|png|jpg|pdf|woff2?|ttf)$'){$removed.Add($relative)}
        else{$kept.Add('ffmpeg-9.0.2/'+$relative)}
    }
    $utf8=New-Object Text.UTF8Encoding($false)
    $list=Join-Path $work 'keep-files.txt'
    [IO.File]::WriteAllLines($list,[string[]]@($kept | Sort-Object),$utf8)
    $name='ffmpeg-9.0.2-nicobe.tar.xz'
    & $tar -cJf (Join-Path $output $name) --options 'xz:compression-level=9' -C $work -T $list
    if($LASTEXITCODE){throw 'FFmpeg source compression failed.'}
    $manifest=[ordered]@{FormatVersion=1;File=$name;SHA256=(Get-FileHash -LiteralPath (Join-Path $output $name)).Hash;UpstreamFile='ffmpeg-9.0.2.tar.xz';UpstreamSHA256=$upstreamHash;ExcludedPaths=@($removed | Sort-Object)}
    [IO.File]::WriteAllText((Join-Path $output 'ffmpeg-source.json'),($manifest | ConvertTo-Json -Depth 3),$utf8)
    foreach($file in @('README.md','build.ps1','reader.c','reader.h','ffmpeg-required-sources.txt')){Copy-Item -LiteralPath (Join-Path $source $file) -Destination $output}
    Copy-Item -LiteralPath (Join-Path $source 'zlib') -Destination (Join-Path $output 'zlib') -Recurse
    Copy-Item -LiteralPath (Join-Path $root 'license\LICENSE') -Destination (Join-Path $output 'LICENSE-NicoBE.txt')
    [IO.File]::WriteAllText((Join-Path $output 'SOURCE-PACKAGE.txt'),@'
NicoBE MKV corresponding source package

FFmpeg 9.0.2 sources used by NicoBE's Windows x64 MKV DLLs are unchanged.
All headers, configure scripts, makefiles and license notices are retained.
Unused codec/filter/format/device/scaling implementation files, documentation
text/assets and selected FATE test data were omitted to reduce download size.
ffmpeg-required-sources.txt records compiler dependencies and configuration
registries that must be kept. This is a source subset, not a full FFmpeg SDK.
ffmpeg-source.json lists every omitted path and both archive checksums.
This package rebuilds NicoBE's MKV DLLs with the supplied configuration.
Other configurations and FATE/documentation targets may need the original
upstream archive. See README.md and build.ps1.
Upstream: https://ffmpeg.org/releases/ffmpeg-9.0.2.tar.xz
'@,$utf8)
    Write-Output ('Prepared MKV source: {0} bytes; {1} unused files omitted.' -f (Get-Item -LiteralPath (Join-Path $output $name)).Length,$removed.Count)
} finally {
    $resolved=[IO.Path]::GetFullPath($work)
    if(!$resolved.StartsWith($tempRoot,[StringComparison]::OrdinalIgnoreCase)){throw 'Unexpected source cleanup path.'}
    if(Test-Path -LiteralPath $resolved){
        if((Get-Item -LiteralPath $resolved -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Source work folder is a link.'}
        if(Get-ChildItem -LiteralPath $resolved -Recurse -Force | Where-Object {$_.Attributes -band [IO.FileAttributes]::ReparsePoint}){throw 'Source work folder contains links.'}
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
