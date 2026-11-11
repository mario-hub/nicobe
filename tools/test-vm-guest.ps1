param([string]$Root='C:\nicobe-vm-test',[switch]$Prepare)
$ErrorActionPreference='Stop'
if((Get-CimInstance Win32_ComputerSystem).Manufacturer -notlike '*VMware*'){throw 'Run this script only in the disposable VMware guest, not on the host.'}
if(Get-Process -Name 'NicoBE','NicoBE.Encoder','VmPlayback','mpc-be64','mpc-be' -ErrorAction SilentlyContinue){throw 'Close guest test applications before starting a new run.'}
$env:DOTNET_ROOT_X64=Join-Path $Root 'dotnet';$env:DOTNET_ROOT=$env:DOTNET_ROOT_X64
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$run=Join-Path $Root 'run1'
if($Prepare){
    if(Test-Path -LiteralPath $run){throw 'Use a fresh guest test directory'}
    Expand-Archive -LiteralPath (Join-Path $Root 'dotnet.zip') -DestinationPath $env:DOTNET_ROOT_X64
    Expand-Archive -LiteralPath (Join-Path $Root 'payload.zip') -DestinationPath $run
    Expand-Archive -LiteralPath (Join-Path $Root 'mpc.zip') -DestinationPath (Join-Path $run 'mpc')
    # INI forces portable preferences; do not reuse any user player settings.
    [IO.File]::WriteAllText((Join-Path $run 'mpc/mpc-be64.ini'),"[Settings]`r`n",[Text.Encoding]::Unicode)
}
New-Item -ItemType Directory -Force -Path (Join-Path $run 'results'),(Join-Path $run 'temp')|Out-Null
$env:TEMP=Join-Path $run 'temp';$env:TMP=$env:TEMP
$results=New-Object 'Collections.Generic.List[object]'
function Save { $results | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $run 'results/guest-results.json') -Encoding UTF8 }
function Case($Name,[scriptblock]$Test){try{$detail=& $Test;$results.Add(@{name=$Name;passed=$true;detail=$detail})}catch{$results.Add(@{name=$Name;passed=$false;error=$_.Exception.ToString()})};Save}
Case 'private-runtime' { $runtime=@(& "$env:DOTNET_ROOT_X64\dotnet.exe" --list-runtimes);if($LASTEXITCODE -ne 0 -or !($runtime -match '^Microsoft.WindowsDesktop.App 10\.')){throw 'Private .NET 10 Desktop Runtime unavailable'};$runtime }
foreach($app in @(@('player','NicoBE.exe'),@('encoder','NicoBE.Encoder.exe'))){
    Case ($app[0]+'-fresh-launch') {
        $exe=Join-Path $run ($app[0]+'/'+$app[1]);$p=Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe) -WindowStyle Hidden -PassThru
        try{Start-Sleep -Seconds 5;$p.Refresh();if($p.HasExited){throw "Early exit: $($p.ExitCode)"};if($p.MainWindowHandle -eq 0 -or $p.MainWindowTitle -notlike '*NicoBE*'){throw 'Application settings window did not appear'};@{pid=$p.Id;title=$p.MainWindowTitle;window=$p.MainWindowHandle.ToInt64();workingSetMiB=$p.WorkingSet64/1MB}}
        finally{if(!$p.HasExited){$p.CloseMainWindow()|Out-Null;if(!$p.WaitForExit(2000)){$p.Kill();$p.WaitForExit()}};$p.Dispose()}
    }
}
foreach($flag in @('frame-sync','opacity','emoji')){
    Case ('guest-gpu-'+$flag){
        $p=Start-Process -FilePath (Join-Path $run 'player/NicoBE.exe') -ArgumentList ('--'+$flag+'-test') -WorkingDirectory (Join-Path $run 'player') -WindowStyle Hidden -PassThru
        try{if(!$p.WaitForExit(45000)){$p.Kill();throw 'Timeout'};if($p.ExitCode -ne 0){throw "Exit: $($p.ExitCode)"};Get-Content (Join-Path $run ('player/logs/'+$flag+'-test.json')) -Raw|ConvertFrom-Json}
        finally{$p.Dispose()}
    }
}
Case 'real-mpc-playback' {
    $p=Start-Process -FilePath (Join-Path $run 'player/VmPlayback.exe') -ArgumentList $run -WorkingDirectory (Join-Path $run 'player') -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $run 'results/playback.stdout.log') -RedirectStandardError (Join-Path $run 'results/playback.stderr.log')
    try{if(!$p.WaitForExit(180000)){$p.Kill();throw 'Timeout'};if($p.ExitCode -ne 0){throw "Exit: $($p.ExitCode); see playback-result.json"};Get-Content (Join-Path $run 'playback-result.json') -Raw | ConvertFrom-Json}
    finally{$p.Dispose()}
}
Save
Compress-Archive -Path (Join-Path $run 'results'),(Join-Path $run 'player/logs'),(Join-Path $run 'playback-result.json') -DestinationPath (Join-Path $Root 'guest-results.zip') -Force
