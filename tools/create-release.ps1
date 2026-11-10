param([string]$OutputDirectory='')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'console-encoding.ps1')
$root = Split-Path -Parent $PSScriptRoot
$releaseFolder = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $root 'release' }
if (!$releaseFolder.StartsWith($root + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) {
    throw 'Release output must stay inside nicobe.'
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.IO.Compression

$commonFiles = @('README.md', 'license/LICENSE', 'license/THIRD-PARTY-NOTICES.txt')
if (Test-Path -LiteralPath (Join-Path $root 'assets\nicobe.ico') -PathType Leaf) {
    $commonFiles += 'assets/nicobe.ico'
}

$packages = @(
    @{
        Name = 'NicoBE-win-x64'
        VersionFile = 'version.txt'
        BuildFolder = 'app'
        IncludeMkv = $true
        Binaries = @(
            'NicoBE.exe', 'NicoBE.dll', 'NicoBE.Gpu.dll',
            'NicoBE.deps.json', 'NicoBE.runtimeconfig.json'
        )
    },
    @{
        Name = 'NicoBE-Encoder-win-x64'
        VersionFile = 'version_encoder.txt'
        BuildFolder = 'app-encoder'
        Binaries = @(
            'NicoBE.Encoder.exe', 'NicoBE.Encoder.dll',
            'NicoBE.Encoder.deps.json', 'NicoBE.Encoder.runtimeconfig.json',
            'NicoBE.Encoder.Cli.exe', 'NicoBE.Encoder.Cli.dll',
            'NicoBE.Encoder.Cli.deps.json', 'NicoBE.Encoder.Cli.runtimeconfig.json'
        )
    }
    @{
        Name = 'NicoBE-MKV-Source'
        SourceOnly = $true
    }
)

$stageRoot=[IO.Path]::GetFullPath((Join-Path $root ('temp\test-results\release-'+[Guid]::NewGuid().ToString('N'))))
$pendingArchives = @{}
try {
    # Build separately from running applications; never overwrite app/app-encoder here.
    $stageParent=Split-Path -Parent $stageRoot
    if((Test-Path -LiteralPath $stageParent) -and ((Get-Item -LiteralPath $stageParent -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Release staging parent must not be a link.'}
    New-Item -ItemType Directory -Path $stageRoot -Force | Out-Null
    # Prepare the public page before the builds, validating release metadata early.
    $stagedPage = Join-Path $stageRoot 'index.html'
    & (Join-Path $PSScriptRoot 'create-release-page.ps1') -OutputPath $stagedPage
    Push-Location -LiteralPath $root
    try {
        & (Join-Path $PSScriptRoot 'build.ps1') -OutputDirectory (Join-Path $stageRoot 'app')
        & (Join-Path $PSScriptRoot 'build-encoder.ps1') -OutputDirectory (Join-Path $stageRoot 'app-encoder')
    } finally {
        Pop-Location
    }
    New-Item -ItemType Directory -Path $releaseFolder -Force | Out-Null
    foreach ($package in $packages) {
        $destination = Join-Path $releaseFolder ($package.Name + '.zip')
        $temporary = Join-Path $releaseFolder ([Guid]::NewGuid().ToString('N') + '.tmp')
        $pendingArchives[$destination] = $temporary
        $archive = [IO.Compression.ZipFile]::Open($temporary, [IO.Compression.ZipArchiveMode]::Create)
        try {
            $prefix = $package.Name + '/'
            $null = $archive.CreateEntry($prefix)
            $files = [ordered]@{}
            if ($package.SourceOnly) {
                $sourceFolder = Join-Path $root 'src\NicoBE.Mkv'
                foreach ($name in @('README.md','build.ps1','reader.c','reader.h','ffmpeg-9.0.2.tar.xz')) {
                    $files[$name] = Join-Path $sourceFolder $name
                }
                $files['LICENSE-NicoBE.txt'] = Join-Path $root 'license\LICENSE'
                foreach ($file in Get-ChildItem -LiteralPath (Join-Path $sourceFolder 'zlib') -File) {
                    $files['zlib/' + $file.Name] = $file.FullName
                }
            } else {
                foreach ($relativePath in $commonFiles) {
                    $files[$relativePath] = Join-Path $root $relativePath
                }
                $files[$package.VersionFile] = Join-Path (Join-Path $root 'release') $package.VersionFile
                $buildFolder = Join-Path $stageRoot $package.BuildFolder
                foreach ($binary in $package.Binaries) {
                    $files[$binary] = Join-Path $buildFolder $binary
                }
                if ($package.IncludeMkv) {
                    foreach ($file in Get-ChildItem -LiteralPath (Join-Path $buildFolder 'mkv') -File) {
                        $files['mkv/' + $file.Name] = $file.FullName
                    }
                }
            }
            foreach ($entryPath in $files.Keys) {
                $null = [IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                    $archive, $files[$entryPath], ($prefix + $entryPath), [IO.Compression.CompressionLevel]::Optimal
                )
            }
        } finally {
            $archive.Dispose()
        }
    }

    $pageDestination = Join-Path $releaseFolder 'index.html'
    $pageTemporary = Join-Path $releaseFolder ([Guid]::NewGuid().ToString('N') + '.tmp')
    $pendingArchives[$pageDestination] = $pageTemporary
    Copy-Item -LiteralPath $stagedPage -Destination $pageTemporary
    foreach ($destination in $pendingArchives.Keys) {
        Move-Item -LiteralPath $pendingArchives[$destination] -Destination $destination -Force
        Write-Host "Created $destination"
    }
} finally {
    foreach ($temporary in $pendingArchives.Values) {
        if (Test-Path -LiteralPath $temporary -PathType Leaf) {
            Remove-Item -LiteralPath $temporary -Force
        }
    }
    # Only remove this run's staging folder after checking the resolved path and links.
    $stagePrefix=[IO.Path]::GetFullPath((Join-Path $root 'temp\test-results'))+[IO.Path]::DirectorySeparatorChar
    if(!$stageRoot.StartsWith($stagePrefix,[StringComparison]::OrdinalIgnoreCase)){throw 'Unexpected release staging path.'}
    if(Test-Path -LiteralPath $stageRoot) {
        if((Get-Item -LiteralPath $stageRoot -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Release staging must not be a link.'}
        if(Get-ChildItem -LiteralPath $stageRoot -Recurse -Force | Where-Object {$_.Attributes -band [IO.FileAttributes]::ReparsePoint}){throw 'Release staging contains a link.'}
        Remove-Item -LiteralPath $stageRoot -Recurse -Force
    }
}
