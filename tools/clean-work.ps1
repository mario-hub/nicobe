[CmdletBinding(SupportsShouldProcess=$true)]
param()

$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$prefix=$root+[IO.Path]::DirectorySeparatorChar
$targets=@('temp','src\NicoBE\obj','src\NicoBE\bin','src\NicoBE.Encoder\obj','src\NicoBE.Encoder\bin','src\NicoBE.Encoder.Cli\obj','src\NicoBE.Encoder.Cli\bin','src\NicoBE.Mkv\.build')
$approved=@()
foreach($relative in $targets){
    $path=[IO.Path]::GetFullPath((Join-Path $root $relative))
    if(!$path.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)){throw "Outside nicobe: $path"}
    if(!(Test-Path -LiteralPath $path)){continue}
    $item=Get-Item -LiteralPath $path -Force
    if(!$item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw "Unexpected cleanup target: $path"}
    $entries=@(Get-ChildItem -LiteralPath $path -Recurse -Force)
    if($entries | Where-Object {$_.Attributes -band [IO.FileAttributes]::ReparsePoint}){throw "Cleanup target contains a link: $path"}
    $files=@($entries | Where-Object {!$_.PSIsContainer})
    $approved+=@{Path=$path;Files=$files.Count;Bytes=($files | Measure-Object Length -Sum).Sum}
}
# Resolve and check every recursive target before deleting any of them.
$count=0;$bytes=0
foreach($target in $approved){
    if($PSCmdlet.ShouldProcess($target.Path,'Remove generated verification/build data')){
        Remove-Item -LiteralPath $target.Path -Recurse -Force
        $count+=$target.Files;$bytes+=$target.Bytes
    }
}
foreach($relative in @('runtime','.packages')){
    $path=[IO.Path]::GetFullPath((Join-Path $root $relative))
    if(!$path.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)){throw "Outside nicobe: $path"}
    if(!(Test-Path -LiteralPath $path -PathType Container)){continue}
    if((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){continue}
    if(!(Get-ChildItem -LiteralPath $path -Force | Select-Object -First 1) -and $PSCmdlet.ShouldProcess($path,'Remove empty generated directory')){
        Remove-Item -LiteralPath $path
    }
}
Write-Output ("Removed {0} files ({1:N1} MiB). Application binaries, settings, downloads and encoder output were preserved." -f $count,($bytes/1MB))
