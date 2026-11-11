# Shared by release creation and isolated package validation (PowerShell 5.1+).
function Get-AppPackages {
    @{
        Name='NicoBE-win-x64';VersionFile='version.txt';BuildFolder='app';IncludeMkv=$true
        Binaries=@('NicoBE.exe','NicoBE.dll','NicoBE.Gpu.dll','NicoBE.deps.json','NicoBE.runtimeconfig.json')
    }
    @{
        Name='NicoBE-Encoder-win-x64';VersionFile='version_encoder.txt';BuildFolder='app-encoder'
        Binaries=@('NicoBE.Encoder.exe','NicoBE.Encoder.dll','NicoBE.Encoder.deps.json','NicoBE.Encoder.runtimeconfig.json',
            'NicoBE.Encoder.Cli.exe','NicoBE.Encoder.Cli.dll','NicoBE.Encoder.Cli.deps.json','NicoBE.Encoder.Cli.runtimeconfig.json')
    }
}
function Get-AppPackageFiles($Root,$BuildFolder,$Package) {
    $files=[ordered]@{}
    foreach($path in @('README.md','license/LICENSE','license/THIRD-PARTY-NOTICES.txt')){$files[$path]=Join-Path $Root $path}
    if(Test-Path -LiteralPath (Join-Path $Root 'assets/nicobe.ico')){$files['assets/nicobe.ico']=Join-Path $Root 'assets/nicobe.ico'}
    $files[$Package.VersionFile]=Join-Path (Join-Path $Root 'release') $Package.VersionFile
    foreach($name in $Package.Binaries){$files[$name]=Join-Path $BuildFolder $name}
    if($Package.IncludeMkv){
        # Never package arbitrary logs/configuration accidentally placed beside native DLLs.
        foreach($name in @('NicoBE.Mkv.dll','avformat-63.dll','avcodec-63.dll','avutil-61.dll','FFmpeg-LGPL-2.1.txt','zlib-LICENSE.txt')){
            $files['mkv/'+$name]=Join-Path (Join-Path $BuildFolder 'mkv') $name
        }
    }
    return $files
}
function Write-PackageEntries($Archive,[string]$Name,$Files) {
    $prefix=$Name+'/'
    $null=$Archive.CreateEntry($prefix)
    foreach($path in $Files.Keys){
        if($path -match '(^/|\\|(^|/)\.\.(/|$)|:)'){throw "Unsafe package entry: $path"}
        if((Get-Item -LiteralPath $Files[$path] -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw "Linked package source: $path"}
        $null=[IO.Compression.ZipFileExtensions]::CreateEntryFromFile($Archive,$Files[$path],($prefix+$path),[IO.Compression.CompressionLevel]::Optimal)
    }
}
