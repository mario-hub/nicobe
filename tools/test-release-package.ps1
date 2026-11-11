param([Parameter(Mandatory=$true)][string]$PlayerBuild,[Parameter(Mandatory=$true)][string]$EncoderBuild,[Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'release-package.ps1')
$root=Split-Path -Parent $PSScriptRoot
$output=[IO.Path]::GetFullPath($OutputDirectory)
$allowed=[IO.Path]::GetFullPath((Join-Path $root 'temp/test-results'))+[IO.Path]::DirectorySeparatorChar
if(!$output.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase)){throw 'Package tests must write inside temp/test-results.'}
$cursor=$output
while($cursor){if((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Linked output is not allowed.'};$cursor=Split-Path -Parent $cursor}
New-Item -ItemType Directory -Force -Path $output | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.IO.Compression
$checks=New-Object 'Collections.Generic.List[string]'
function Bytes-Hash([byte[]]$Bytes){$sha=[Security.Cryptography.SHA256]::Create();try{return [BitConverter]::ToString($sha.ComputeHash($Bytes))}finally{$sha.Dispose()}}
function Check-Archive($Path,$Package,$Files,$Build) {
    $zip=[IO.Compression.ZipFile]::OpenRead($Path)
    try{
        $prefix=$Package.Name+'/'
        $names=@($zip.Entries | ForEach-Object FullName)
        if(@($names | Sort-Object -Unique).Count -ne $names.Count){throw 'Duplicate archive entry'}
        $expected=@($prefix)+@($Files.Keys | ForEach-Object {$prefix+$_})
        if(Compare-Object $expected $names){throw 'Unexpected or missing archive entries'}
        foreach($required in @('license/LICENSE','license/THIRD-PARTY-NOTICES.txt',$Package.VersionFile)){
            if(!$zip.GetEntry($prefix+$required)){throw "Missing license/version: $required"}
        }
        $applications=if($Package.IncludeMkv){@('NicoBE')}else{@('NicoBE.Encoder','NicoBE.Encoder.Cli')}
        foreach($application in $applications){foreach($suffix in @('.exe','.dll','.deps.json','.runtimeconfig.json')){
            if(!$zip.GetEntry($prefix+$application+$suffix)){throw "Missing application file: $application$suffix"}
        }}
        if($Package.IncludeMkv){
            foreach($required in @('NicoBE.Gpu.dll','mkv/NicoBE.Mkv.dll','mkv/avformat-63.dll','mkv/avcodec-63.dll','mkv/avutil-61.dll','mkv/FFmpeg-LGPL-2.1.txt','mkv/zlib-LICENSE.txt')){
                if(!$zip.GetEntry($prefix+$required)){throw "Missing native dependency/license: $required"}
            }
        }
        $versionText=[IO.File]::ReadAllText((Join-Path (Join-Path $root 'release') $Package.VersionFile))
        if($versionText -notmatch '(?m)^## (\d+\.\d+\.\d+) \((\d{4}-\d{2}-\d{2})\)'){throw 'Invalid version definition'}
        $version=$Matches[1]+'.0'
        foreach($binary in $Package.Binaries | Where-Object {$_ -match '\.exe$|(?<!Gpu)\.dll$'}){
            if([Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $Build $binary)).FileVersion -ne $version){throw "Binary version mismatch: $binary"}
        }
        $latin=[Text.Encoding]::GetEncoding(28591)
        $privatePaths=@($root,$env:USERPROFILE) | Where-Object {$_}
        foreach($relative in $Files.Keys){
            $entry=$zip.GetEntry($prefix+$relative);$stream=$entry.Open();$memory=New-Object IO.MemoryStream
            try{$stream.CopyTo($memory);$bytes=$memory.ToArray()}finally{$stream.Dispose();$memory.Dispose()}
            $raw=$latin.GetString($bytes)
            foreach($path in $privatePaths){foreach($variant in @($path,$path.Replace('\','/'))){
                foreach($needle in @($variant,$latin.GetString([Text.Encoding]::Unicode.GetBytes($variant)))){
                    if($raw.IndexOf($needle,[StringComparison]::OrdinalIgnoreCase) -ge 0){throw "Local path leak: $relative"}
                }
            }}
            if($relative -eq $Package.VersionFile -and [Text.Encoding]::UTF8.GetString($bytes).Trim([char]0xfeff) -ne $versionText.Trim([char]0xfeff)){throw 'Version/history mismatch'}
            if((Bytes-Hash $bytes) -ne (Bytes-Hash ([IO.File]::ReadAllBytes($Files[$relative])))){throw "Package content mismatch: $relative"}
        }
    }finally{$zip.Dispose()}
}
function New-Archive($Path,$Package,$Files){
    $zip=[IO.Compression.ZipFile]::Open($Path,[IO.Compression.ZipArchiveMode]::Create)
    try{Write-PackageEntries $zip $Package.Name $Files}finally{$zip.Dispose()}
}
try{
    foreach($package in Get-AppPackages){
        $build=if($package.IncludeMkv){$PlayerBuild}else{$EncoderBuild}
        $files=Get-AppPackageFiles $root $build $package
        $path=Join-Path $output ($package.Name+'.zip')
        New-Archive $path $package $files
        Check-Archive $path $package $files $build
        $checks.Add($package.Name+': exact entries, binaries, versions/history, licenses, hashes and no workspace/user paths')
        foreach($bad in @('missing-license','missing-binary','settings','logs','local-path','version','duplicate','traversal')){
            $broken=Join-Path $output 'invalid.zip';Copy-Item -LiteralPath $path -Destination $broken
            $zip=[IO.Compression.ZipFile]::Open($broken,[IO.Compression.ZipArchiveMode]::Update)
            try{
                $prefix=$package.Name+'/'
                switch($bad){
                    'missing-license'{$zip.GetEntry($prefix+'license/LICENSE').Delete()}
                    'missing-binary'{$zip.GetEntry($prefix+$package.Binaries[0]).Delete()}
                    'settings'{$null=$zip.CreateEntry($prefix+'encoder-settings.json')}
                    'logs'{$null=$zip.CreateEntry($prefix+'logs/private.log')}
                    'duplicate'{$null=$zip.CreateEntry($prefix+'README.md')}
                    'traversal'{$null=$zip.CreateEntry($prefix+'../outside.txt')}
                    default{
                        $name=if($bad -eq 'version'){$package.VersionFile}else{'README.md'}
                        $zip.GetEntry($prefix+$name).Delete();$entry=$zip.CreateEntry($prefix+$name)
                        $writer=New-Object IO.StreamWriter($entry.Open())
                        try{$writer.Write($(if($bad -eq 'version'){'## 9.9.9 (2000-01-01)'}else{$root}))}finally{$writer.Dispose()}
                    }
                }
            }finally{$zip.Dispose()}
            $rejected=$false
            try{Check-Archive $broken $package $files $build}catch{$rejected=$true}
            if(!$rejected){throw "Validator accepted $bad"}
            $checks.Add($package.Name+': rejected '+$bad)
            Remove-Item -LiteralPath $broken
        }
    }
    @{passed=$true;checks=@($checks)} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'result.json') -Encoding UTF8
}catch{
    @{passed=$false;checks=@($checks);error=$_.Exception.ToString()} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'result.json') -Encoding UTF8
    throw
}
