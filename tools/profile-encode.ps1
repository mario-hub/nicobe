param([Parameter(Mandatory=$true)][string]$InputFile,[string[]]$Names=@(),[int]$Seconds=300,[ValidatePattern('^[a-z0-9-]*$')][string]$Tag='')
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot
$out=Join-Path $root ('temp\test-results\encode-profile-'+[DateTime]::Now.ToString('yyyyMMdd-HHmmss-fff'))
New-Item -ItemType Directory -Force -Path $out | Out-Null
$env:TEMP=Join-Path $root 'temp';$env:TMP=$env:TEMP
$settings=Get-Content (Join-Path $root 'app-encoder\encoder-settings.json') -Raw | ConvertFrom-Json
if(-not (Test-Path -LiteralPath $InputFile -PathType Leaf)){throw 'InputFile does not exist.'}
$common=@('-hide_banner','-nostdin','-n','-loglevel','info','-benchmark','-probesize','32000000','-analyzeduration','10000000','-copyts','-start_at_zero','-i',$inputFile,'-map','0:1','-sn','-dn','-map_metadata','-1','-map_chapters','-1')
$deint='bwdif=mode=send_frame:parity=auto:deint=interlaced'
$scale='scale=1600:900:force_original_aspect_ratio=decrease:force_divisible_by=2:reset_sar=1,pad=1600:900:(ow-iw)/2:(oh-ih)/2,setsar=1'
$filter="$deint,$scale,fps=fps=30:round=near"
$tail=@('-t',"$Seconds",'-progress','pipe:1','-stats_period','0.5','-nostats')
$raw=@('-c:v','wrapped_avframe','-threads:v','8','-pix_fmt','yuv420p','-fps_mode','passthrough')
$nvenc=@('-c:v','hevc_nvenc','-preset','p4','-rc','vbr','-b:v','1M','-threads:v','8','-pix_fmt','yuv420p','-fps_mode','passthrough')
$cases=@(
    @{Name='01_read_demux';Options=@('-an','-c:v','copy');Mux=@('-f','null','-')},
    @{Name='02_decode';Options=@('-an')+$raw;Mux=@('-f','null','-')},
    @{Name='03_deinterlace';Options=@('-an')+$raw+@('-vf',$deint);Mux=@('-f','null','-')},
    @{Name='04_resize';Options=@('-an')+$raw+@('-vf',$scale);Mux=@('-f','null','-')},
    @{Name='05_all_filters';Options=@('-an')+$raw+@('-vf',$filter);Mux=@('-f','null','-')},
    @{Name='06_nvenc_video';Options=@('-an')+$nvenc+@('-vf',$filter,'-multipass','disabled');Mux=@('-f','null','-')},
    @{Name='07_full_1pass';Options=@('-map','0:2','-c:a','aac','-b:a','128k')+$nvenc+@('-vf',$filter,'-multipass','disabled','-tag:v','hvc1');Mux=@('-movflags','+faststart+use_metadata_tags',(Join-Path $out 'full-1pass.mp4'))},
    @{Name='08_full_2pass';Options=@('-map','0:2','-c:a','aac','-b:a','128k')+$nvenc+@('-vf',$filter,'-multipass','fullres','-tag:v','hvc1');Mux=@('-movflags','+faststart+use_metadata_tags',(Join-Path $out 'full-2pass.mp4'))},
    @{Name='09_filter_threads4';Options=@('-map','0:2','-c:a','aac','-b:a','128k')+$nvenc+@('-filter_threads','4','-vf',$filter,'-multipass','disabled','-tag:v','hvc1');Mux=@('-movflags','+faststart+use_metadata_tags',(Join-Path $out 'threads4.mp4'))},
    @{Name='10_filter_threads8';Options=@('-map','0:2','-c:a','aac','-b:a','128k')+$nvenc+@('-filter_threads','8','-vf',$filter,'-multipass','disabled','-tag:v','hvc1');Mux=@('-movflags','+faststart+use_metadata_tags',(Join-Path $out 'threads8.mp4'))}
)
if($Names.Count){$cases=@(foreach($name in $Names){$cases | Where-Object {$_.Name -eq $name}})}else{$cases=$cases[0..7]}
function Start-Hidden([string]$exe,[string[]]$arguments){
    $info=[Diagnostics.ProcessStartInfo]::new($exe);$info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true;$info.WorkingDirectory=$root
    foreach($argument in $arguments){$info.ArgumentList.Add($argument)}
    return [Diagnostics.Process]::Start($info)
}
foreach($case in $cases){
    if($Tag){$case.Name+='-'+$Tag;$case.Mux=@($case.Mux | ForEach-Object {if($_.EndsWith('.mp4')){[IO.Path]::ChangeExtension($_,$null).TrimEnd('.')+'-'+$Tag+'.mp4'}else{$_}})}
    $arguments=$common+$case.Options+$tail+$case.Mux
    $arguments | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $out ($case.Name+'-arguments.json'))
    $monitor=Start-Hidden 'C:\Windows\System32\nvidia-smi.exe' @('--query-gpu=timestamp,utilization.gpu,utilization.encoder,utilization.decoder,memory.used','--format=csv,noheader,nounits','-lms','200')
    $gpuRead=$monitor.StandardOutput.ReadToEndAsync();$gpuErrors=$monitor.StandardError.ReadToEndAsync()
    $samples=[Collections.Generic.List[object]]::new();$clock=[Diagnostics.Stopwatch]::StartNew();$process=$null
    try{
        $process=Start-Hidden $settings.Ffmpeg $arguments
        $stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync()
        while(-not $process.WaitForExit(200)){
            $process.Refresh();$samples.Add([pscustomobject]@{Seconds=$clock.Elapsed.TotalSeconds;CpuSeconds=$process.TotalProcessorTime.TotalSeconds;WorkingSetMB=$process.WorkingSet64/1MB})
            if($clock.Elapsed.TotalSeconds -gt 180){$process.Kill($true);throw 'Benchmark timed out.'}
        }
        $clock.Stop();$exit=$process.ExitCode;$cpu=$process.TotalProcessorTime.TotalSeconds
        $errorText=$stderr.GetAwaiter().GetResult();$progress=$stdout.GetAwaiter().GetResult()
        $errorText | Set-Content -LiteralPath (Join-Path $out ($case.Name+'.log'))
        $progress | Set-Content -LiteralPath (Join-Path $out ($case.Name+'-progress.txt'))
    }finally{
        if($null -ne $process -and -not $process.HasExited){$process.Kill($true);$process.WaitForExit()}
        if(-not $monitor.HasExited){$monitor.Kill();$monitor.WaitForExit()}
        $gpuText=$gpuRead.GetAwaiter().GetResult();$gpuText | Set-Content -LiteralPath (Join-Path $out ($case.Name+'-gpu.csv'))
        $gpuErrors.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $out ($case.Name+'-gpu-errors.txt'))
        $monitor.Dispose()
    }
    $samples | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $out ($case.Name+'-cpu.json'))
    $gpuRows=@($gpuText -split '\r?\n' | Where-Object {$_} | ConvertFrom-Csv -Header Time,Gpu,Encoder,Decoder,Memory)
    $result=[ordered]@{Name=$case.Name;ExitCode=$exit;InputSeconds=$Seconds;WallSeconds=[Math]::Round($clock.Elapsed.TotalSeconds,3);CpuSeconds=[Math]::Round($cpu,3);CpuPercent=[Math]::Round(100*$cpu/$clock.Elapsed.TotalSeconds/[Environment]::ProcessorCount,1);LogicalProcessors=[Environment]::ProcessorCount;Speed=[Math]::Round($Seconds/$clock.Elapsed.TotalSeconds,2);GpuSamples=$gpuRows.Count}
    foreach($field in @('Gpu','Encoder','Decoder')){
        $values=@($gpuRows | ForEach-Object {if($_.$field.Trim() -match '^\d+$'){[double]$_.$field}})
        $result[$field+'Average']=if($values.Count){[Math]::Round(($values | Measure-Object -Average).Average,1)}else{$null}
        $result[$field+'Peak']=if($values.Count){($values | Measure-Object -Maximum).Maximum}else{$null}
    }
    $result | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $out ($case.Name+'-result.json'))
    $result | ConvertTo-Json -Compress
    $process.Dispose()
    if($exit -ne 0){throw ('FFmpeg failed: '+$case.Name)}
}
