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

. (Join-Path $PSScriptRoot 'release-package.ps1')
$packages = @(Get-AppPackages) + @(@{Name='NicoBE-MKV-Source';SourceOnly=$true})

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
            $files = [ordered]@{}
            if ($package.SourceOnly) {
                $sourceFolder = Join-Path $stageRoot 'mkv-source'
                & (Join-Path $PSScriptRoot 'prepare-mkv-source.ps1') -OutputDirectory $sourceFolder
                foreach ($file in Get-ChildItem -LiteralPath $sourceFolder -File -Recurse) {
                    $files[$file.FullName.Substring($sourceFolder.Length+1).Replace('\','/')] = $file.FullName
                }
            } else {
                $files = Get-AppPackageFiles $root (Join-Path $stageRoot $package.BuildFolder) $package
            }
            Write-PackageEntries $archive $package.Name $files
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
