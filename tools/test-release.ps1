param([string]$Ffmpeg='', [switch]$SkipGpu, [switch]$SkipMedia, [Alias('?')][switch]$Help)
if($Help){
    Write-Output 'Usage: release\test-release.bat [-Ffmpeg <ffmpeg.exe>] [-SkipGpu] [-SkipMedia] [-Help]'
    Write-Output 'Exit: 0=all automated checks passed, 1=failure, 2=automated checks skipped. Manual checks remain separate.'
    exit 0
}
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'console-encoding.ps1')
$root=Split-Path -Parent $PSScriptRoot
# Refuse links before creating or copying anything into the workspace.
function Assert-PlainPath([string]$Path) {
    $current=[IO.Path]::GetFullPath($Path)
    while($current){
        if((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw "Reparse point is not allowed: $current"}
        $current=Split-Path -Parent $current
    }
}
Assert-PlainPath (Join-Path $root 'temp/test-results')
$run=Join-Path $root ('temp/test-results/release-test-'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+[guid]::NewGuid().ToString('N').Substring(0,6))
New-Item -ItemType Directory -Path $run,(Join-Path $run 'scratch') | Out-Null
$env:TEMP=Join-Path $run 'scratch';$env:TMP=$env:TEMP
Add-Type -Path (Join-Path $root 'src/ReleaseTests/ProcessRunner.cs')
$utf8=New-Object Text.UTF8Encoding($false)
$rows=New-Object 'Collections.Generic.List[object]'
$ps=(Get-Process -Id $PID).Path
$playerBuild=Join-Path $run 'build/player';$encoderBuild=Join-Path $run 'build/encoder'
$testFfmpeg='';$fixture='';$fixtureLogs=''
function Write-Text($Path,$Text){[IO.File]::WriteAllText($Path,$Text,$utf8)}
function Environment-For([string]$Directory,[bool]$App=$false){
    $e=New-Object 'Collections.Generic.Dictionary[string,string]'
    $e['TEMP']=$env:TEMP;$e['TMP']=$env:TEMP
    # Do not inherit a caller's test context into builds or unrelated subprocesses.
    $e['NICOBE_RELEASE_TEST_ROOT']='';$e['NICOBE_TEST_FFMPEG']=''
    $e['DOTNET_CLI_TELEMETRY_OPTOUT']='1';$e['MSBUILDDISABLENODEREUSE']='1'
    if($App){$e['NICOBE_RELEASE_TEST_ROOT']=$Directory;$e['NICOBE_TEST_FFMPEG']=$testFfmpeg}
    return ,$e
}
function Invoke-Child($Exe,[string[]]$Arguments,$Directory,$Prefix,[int]$Timeout=120,$Environment=$null){
    if($null -eq $Environment){$Environment=Environment-For $Directory}
    $result=[NicoBE.ReleaseTests.ProcessRunner]::Run($Exe,$Arguments,$Directory,$Environment,$Prefix,$Timeout)
    if($result.TimedOut){throw "Timeout after $Timeout seconds; owned process tree terminated. See $Prefix.stderr.log"}
    if($result.ExitCode -ne 0){throw "Exit code $($result.ExitCode). See $Prefix.stderr.log and $Prefix.stdout.log"}
}
function Row($Id,$Status,$Detail,$Category='Automatic',$Seconds=0){
    $rows.Add([pscustomobject]@{id=$Id;status=$Status;category=$Category;seconds=[math]::Round($Seconds,2);detail=$Detail})
    Write-Host ('[{0}] {1}: {2}' -f $Status,$Id,$Detail)
}
function Test-Case($Id,[scriptblock]$Action){
    $watch=[Diagnostics.Stopwatch]::StartNew()
    try{& $Action;Row $Id 'PASS' 'OK' 'Automatic' $watch.Elapsed.TotalSeconds;return $true}
    catch{Row $Id 'FAIL' $_.Exception.Message 'Automatic' $watch.Elapsed.TotalSeconds;return $false}
}
function Copy-PlainTree($Source,$Destination){
    Assert-PlainPath $Source;Assert-PlainPath $Destination
    $items=@(Get-ChildItem -LiteralPath $Source -Recurse -Force)
    if($items | Where-Object {$_.Attributes -band [IO.FileAttributes]::ReparsePoint}){throw "Linked file in $Source"}
    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    Get-ChildItem -LiteralPath $Source -Force | Copy-Item -Destination $Destination -Recurse
}
function New-AppCase($Id,$Build){
    $dir=Join-Path $run ('cases/'+$Id);Copy-PlainTree $Build $dir
    New-Item -ItemType Directory -Force -Path (Join-Path $dir 'temp/test-results') | Out-Null
    Write-Text (Join-Path $dir '.release-test') 'NicoBE isolated release test'
    $logs=if($fixtureLogs){$fixtureLogs}else{Join-Path $dir 'empty-nicojk'}
    Write-Text (Join-Path $dir 'nicobe.json') (@{NicoJkLogFolder=$logs;AutoFetchPastLogs=$false;ChannelOverrides=@{'15:1024'='jk1';'0:1024'='jk1'}} | ConvertTo-Json -Depth 5)
    Write-Text (Join-Path $dir 'encoder-settings.json') (@{Ffmpeg=$testFfmpeg;NicoJkLogFolder=$logs;UsePastLogComments=$false;Codec='H264';Backend='CPU';Passes=1;FrameRate=30;Width=1280;Height=720;Speed='Fast';RateControl='Quality';OutputFolder=(Join-Path $dir 'output')} | ConvertTo-Json)
    return $dir
}
function App-Test($Id,$Build,$Exe,$Flag,$Report,[string[]]$Extra=@(),[int]$Timeout=120){
    $null=Test-Case $Id {
        $dir=New-AppCase $Id $Build
        Invoke-Child (Join-Path $dir $Exe) (@($Flag)+$Extra) $dir (Join-Path $dir 'process') $Timeout (Environment-For $dir $true)
        if($Report){
            $path=Join-Path $dir $Report
            if(!(Test-Path -LiteralPath $path)){throw "Expected test report missing: $Report"}
            $data=Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json
            if($data.passed -ne $true){throw "Test report did not pass: $Report"}
        }
    }
}
function Save-Report {
    $failed=@($rows | Where-Object status -eq 'FAIL').Count
    $skipped=@($rows | Where-Object {$_.status -eq 'SKIP' -and $_.category -eq 'Automatic'}).Count
    $passed=@($rows | Where-Object status -eq 'PASS').Count
    $manual=@($rows | Where-Object category -eq 'Manual').Count
    $script:resultCode=if($failed){1}elseif($skipped){2}else{0}
    $summary="PASS: $passed / FAIL: $failed / SKIP (automatic): $skipped / Manual pending: $manual / Exit: $script:resultCode"
    $report=@{created=(Get-Date -Format o);directory=$run;exitCode=$script:resultCode;summary=$summary;results=$rows.ToArray()}
    Write-Text (Join-Path $run 'results.json') ($report | ConvertTo-Json -Depth 8)
    Write-Text (Join-Path $run 'summary.txt') ($summary+"`r`n"+($rows | Format-Table status,id,category,seconds,detail -Wrap | Out-String -Width 180))
    $body=($rows | ForEach-Object {'<tr><td class="'+$_.status+'">'+$_.status+'</td><td>'+[Net.WebUtility]::HtmlEncode($_.id)+'</td><td>'+[Net.WebUtility]::HtmlEncode($_.category)+'</td><td>'+$_.seconds+'</td><td>'+[Net.WebUtility]::HtmlEncode($_.detail)+'</td></tr>'}) -join "`n"
    Write-Text (Join-Path $run 'report.html') ('<!doctype html><html lang="ja"><meta charset="utf-8"><title>NicoBE release tests</title><style>body{font:16px/1.6 system-ui;background:#1c2025;color:#eee;margin:32px}table{border-collapse:collapse;width:100%}td,th{padding:9px;text-align:left;border-bottom:1px solid #49535d;vertical-align:top}.PASS{color:#7ee0af}.FAIL{color:#ff9292}.SKIP{color:#e6c96e}a{color:#8ee3ee}</style><h1>NicoBE リリース前テスト</h1><p>'+[Net.WebUtility]::HtmlEncode($summary)+'</p><p>合成データと模擬通信を使用。SKIP は未検証です。Manual は実再生などの手動確認項目で、終了コードには含みません。</p><p><a href="results.json">JSON</a> / <a href="summary.txt">テキスト一覧</a> / <a href="cases/">テスト別ログ・画像</a></p><table><thead><tr><th>結果</th><th>項目</th><th>区分</th><th>秒</th><th>詳細</th></tr></thead><tbody>'+$body+'</tbody></table></html>')
    Write-Host "`n$summary`nReport: $(Join-Path $run 'report.html')"
}
Write-Host "Isolated release tests: $run"
try {
    $null=Test-Case 'runner-supervision' {
        $prefix=Join-Path $run 'supervisor'
        $r=[NicoBE.ReleaseTests.ProcessRunner]::Run($ps,@('-NoProfile','-Command','[Console]::Out.Write((''o''*100000));[Console]::Error.Write((''e''*100000));exit 7'),$run,(Environment-For $run),$prefix,20)
        if($r.ExitCode -ne 7 -or $r.TimedOut -or (Get-Item "$prefix.stdout.log").Length -lt 100000 -or (Get-Item "$prefix.stderr.log").Length -lt 100000){throw 'Exit code / pipe drainage check failed'}
        $r=[NicoBE.ReleaseTests.ProcessRunner]::Run($ps,@('-NoProfile','-Command','Start-Sleep -Seconds 30'),$run,(Environment-For $run),($prefix+'-timeout'),1)
        if(!$r.TimedOut -or $r.Seconds -gt 15){throw 'Timeout check failed'}
        $childScript=Join-Path $run 'child-process-test.ps1';$pidFile=Join-Path $run 'owned-child.pid'
        Write-Text $childScript 'param($PidFile) $child=Start-Process -FilePath (Get-Process -Id $PID).Path -ArgumentList @("-NoProfile","-Command","Start-Sleep -Seconds 30") -WindowStyle Hidden -PassThru; [IO.File]::WriteAllText($PidFile,$child.Id.ToString())'
        Invoke-Child $ps @('-NoProfile','-ExecutionPolicy','Bypass','-File',$childScript,$pidFile) $run ($prefix+'-child') 20
        $childPid=[int][IO.File]::ReadAllText($pidFile)
        $deadline=[DateTime]::UtcNow.AddSeconds(3)
        while((Get-Process -Id $childPid -ErrorAction SilentlyContinue) -and [DateTime]::UtcNow -lt $deadline){Start-Sleep -Milliseconds 50}
        if(Get-Process -Id $childPid -ErrorAction SilentlyContinue){throw 'Owned child process was not terminated'}
    }
    $playerOk=Test-Case 'build-player' {Invoke-Child $ps @('-NoProfile','-ExecutionPolicy','Bypass','-File',(Join-Path $PSScriptRoot 'build.ps1'),'-OutputDirectory',$playerBuild) $root (Join-Path $run 'build-player') 600}
    $encoderOk=Test-Case 'build-encoder-cli' {Invoke-Child $ps @('-NoProfile','-ExecutionPolicy','Bypass','-File',(Join-Path $PSScriptRoot 'build-encoder.ps1'),'-OutputDirectory',$encoderBuild) $root (Join-Path $run 'build-encoder') 600}
    if($playerOk){
        foreach($name in @('self','storage','failure','live','pastlog','ass','settings','visibility','playback-panel','mpc-control','playback-lifecycle','scene-continuity')){
            $report=if($name -eq 'live'){'live-comment-test.json'}else{$name+'-test.json'}
            App-Test ('player-'+$name) $playerBuild 'NicoBE.exe' ('--'+$name+'-test') ('logs/'+$report)
        }
        foreach($name in @('frame-sync','emoji','outline','opacity','gpu-recovery')){
            if($SkipGpu){Row ('player-'+$name) 'SKIP' '-SkipGpu specified'}
            else{App-Test ('player-'+$name) $playerBuild 'NicoBE.exe' ('--'+$name+'-test') ('logs/'+$name+'-test.json')}
        }
    }else{Row 'player-regressions' 'SKIP' 'Player build failed'}
    if($encoderOk){
        foreach($test in @(
            @('failure','logs/failure-test.json'),@('storage','logs/storage-test.json'),@('pipe','logs/pipe-failure-test.json'),
            @('installer','temp/test-results/installer-test.json'),@('comment-format','temp/test-results/comment-format/result.json'),
            @('ass-style','logs/ass-style-test.json'),@('ui','temp/test-results/encoder-ui-checks.json'),@('guide','temp/test-results/encoder-guide-test.json'))){
            App-Test ('encoder-'+$test[0]) $encoderBuild 'NicoBE.Encoder.exe' ('--'+$test[0]+'-test') $test[1]
        }
        App-Test 'cli-arguments' $encoderBuild 'NicoBE.Encoder.Cli.exe' '--self-test' ''
    }else{Row 'encoder-regressions' 'SKIP' 'Encoder/CLI build failed'}
    # Only executable tools are read from an existing installation. Never read its settings.
    if(!$SkipMedia -and !$Ffmpeg){
        $installed=Join-Path $root 'app-encoder/ffmpeg'
        if(Test-Path -LiteralPath $installed){Assert-PlainPath $installed;$Ffmpeg=Get-ChildItem -LiteralPath $installed -Filter ffmpeg.exe -File -Recurse | Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName}
    }
    $mediaReady=$false
    if($SkipMedia){Row 'media-prerequisites' 'SKIP' '-SkipMedia specified'}
    elseif(!$Ffmpeg){Row 'media-prerequisites' 'SKIP' 'FFmpeg not found; use -Ffmpeg <ffmpeg.exe> with adjacent ffprobe.exe'}
    elseif(!$encoderOk){Row 'media-prerequisites' 'SKIP' 'Encoder build failed'}
    else{
        $mediaReady=Test-Case 'synthetic-media-fixture' {
            $Ffmpeg=[IO.Path]::GetFullPath($Ffmpeg);Assert-PlainPath $Ffmpeg
            $sourceDir=Split-Path -Parent $Ffmpeg;$toolDir=Join-Path $run 'ffmpeg'
            New-Item -ItemType Directory -Path $toolDir | Out-Null
            foreach($name in @('ffmpeg.exe','ffprobe.exe')){Assert-PlainPath (Join-Path $sourceDir $name);Copy-Item -LiteralPath (Join-Path $sourceDir $name) -Destination $toolDir}
            Get-ChildItem -LiteralPath $sourceDir -Filter '*.dll' -File | ForEach-Object {Assert-PlainPath $_.FullName;Copy-Item -LiteralPath $_.FullName -Destination $toolDir}
            $script:testFfmpeg=Join-Path $toolDir 'ffmpeg.exe'
            $fixtures=Join-Path $run 'fixtures';New-Item -ItemType Directory -Path $fixtures | Out-Null
            $raw=Join-Path $fixtures 'raw.ts'
            Invoke-Child $testFfmpeg @('-v','error','-nostdin','-y','-f','lavfi','-i','testsrc2=size=320x180:rate=30','-f','lavfi','-i','sine=frequency=440:sample_rate=48000','-t','4','-c:v','mpeg2video','-b:v','1M','-c:a','aac','-mpegts_original_network_id','15','-mpegts_service_id','1024','-f','mpegts',$raw) $fixtures (Join-Path $fixtures 'generate') 60
            # Insert one TDT immediately after the first PCR. FFmpeg does not emit broadcast wall-clock time.
            $bytes=[IO.File]::ReadAllBytes($raw);$packet=New-Object byte[] 188
            for($i=0;$i -lt 188;$i++){$packet[$i]=255}
            $date=[datetime]::new(2020,1,2,3,4,5);$mjd=[int]($date.Date-[datetime]::new(1858,11,17)).TotalDays
            [byte[]]$head=@(0x47,0x40,0x14,0x10,0,0x70,0x70,5,($mjd -shr 8),($mjd -band 255),3,4,5)
            [array]::Copy($head,$packet,$head.Length)
            $offset=-1
            for($i=0;$i -lt $bytes.Length;$i+=188){if($bytes[$i] -ne 0x47){throw 'Invalid generated TS'};if(($bytes[$i+3] -band 0x20) -and $bytes[$i+4] -ge 7 -and ($bytes[$i+5] -band 0x10)){$offset=$i+188;break}}
            if($offset -lt 0){throw 'PCR missing from generated TS'}
            $script:fixture=Join-Path $fixtures 'synthetic.ts'
            $stream=[IO.File]::Create($fixture)
            try{$stream.Write($bytes,0,$offset);$stream.Write($packet,0,188);$stream.Write($bytes,$offset,$bytes.Length-$offset)}finally{$stream.Dispose()}
            $script:fixtureLogs=Join-Path $fixtures 'nicojk';$channel=Join-Path $fixtureLogs 'jk1';New-Item -ItemType Directory -Path $channel -Force | Out-Null
            $stamp=[DateTimeOffset]::new($date,[TimeSpan]::FromHours(9)).ToUnixTimeSeconds()
            $comments=0..3 | ForEach-Object {'<chat date="'+($stamp+$_)+'" no="'+($_+1)+'" user_id="synthetic" mail="">Synthetic comment '+$_+'</chat>'}
            Write-Text (Join-Path $channel ($stamp.ToString()+'.txt')) ($comments -join "`n")
        }
    }
    if($mediaReady){
        App-Test 'encoder-interruption' $encoderBuild 'NicoBE.Encoder.exe' '--encode-interruption-test' 'logs/encode-interruption-test.json' @($fixture) 180
        App-Test 'encoder-formats' $encoderBuild 'NicoBE.Encoder.exe' '--format-test' 'temp/test-results/format-test.json' @($fixture) 600
        App-Test 'encoder-no-comments' $encoderBuild 'NicoBE.Encoder.exe' '--no-comments-test' 'temp/test-results/no-comments/result.json' @($fixture) 180
        App-Test 'encoder-audio' $encoderBuild 'NicoBE.Encoder.exe' '--audio-test' 'temp/test-results/audio-test/checks.json' @() 180
        App-Test 'encoder-av1' $encoderBuild 'NicoBE.Encoder.exe' '--av1-test' 'temp/test-results/av1-test.json' @($fixture) 600
        $av1=Join-Path $run 'cases/encoder-av1/temp/test-results/av1-test.json'
        if(Test-Path $av1){$availability=(Get-Content $av1 -Raw -Encoding UTF8 | ConvertFrom-Json).availability;foreach($backend in @('NVENC','QSV','AMF')){if($null -ne $availability -and $availability.PSObject.Properties[$backend] -and $availability.$backend -eq $false){Row ('av1-'+$backend) 'SKIP' 'Hardware encoder unavailable (capability/fallback probe attempted)'}}}
        $null=Test-Case 'cli-real-encode' {
            $dir=New-AppCase 'cli-real-encode' $encoderBuild
            $cwd=Join-Path $dir 'different-working-directory';New-Item -ItemType Directory -Path $cwd | Out-Null
            Copy-Item -LiteralPath $fixture -Destination (Join-Path $cwd 'input.ts')
            $settings=Get-Content (Join-Path $dir 'encoder-settings.json') -Raw -Encoding UTF8 | ConvertFrom-Json
            $settings.Codec='H265';$settings.Passes=2;$settings.RateControl='Bitrate'
            Write-Text (Join-Path $dir 'encoder-settings.json') ($settings | ConvertTo-Json -Depth 8)
            $before=Get-FileHash (Join-Path $dir 'encoder-settings.json')
            Invoke-Child (Join-Path $dir 'NicoBE.Encoder.Cli.exe') @('input.ts','output.mp4') $cwd (Join-Path $dir 'process') 240 (Environment-For $dir $true)
            if(!(Test-Path (Join-Path $cwd 'output.mp4'))){throw 'Relative output missing'}
            if((Get-FileHash (Join-Path $dir 'encoder-settings.json')).Hash -ne $before.Hash){throw 'CLI changed saved settings'}
            Invoke-Child (Join-Path (Split-Path $testFfmpeg) 'ffprobe.exe') @('-v','error','-show_streams','-of','json',(Join-Path $cwd 'output.mp4')) $cwd (Join-Path $dir 'probe')
            $probe=Get-Content (Join-Path $dir 'probe.stdout.log') -Raw -Encoding UTF8 | ConvertFrom-Json
            if(!($probe.streams | Where-Object codec_name -eq 'hevc')){throw 'CLI did not encode HEVC'}
        }
    }else{foreach($id in @('encoder-interruption','encoder-formats','encoder-no-comments','encoder-audio','encoder-av1','cli-real-encode')){Row $id 'SKIP' 'Synthetic-media prerequisites not available'}}
    if($playerOk -and $encoderOk){$null=Test-Case 'release-package' {
        Invoke-Child $ps @('-NoProfile','-ExecutionPolicy','Bypass','-File',(Join-Path $PSScriptRoot 'test-release-package.ps1'),'-PlayerBuild',$playerBuild,'-EncoderBuild',$encoderBuild,'-OutputDirectory',(Join-Path $run 'packages')) $root (Join-Path $run 'packages') 120
        if((Get-Content (Join-Path $run 'packages/result.json') -Raw -Encoding UTF8 | ConvertFrom-Json).passed -ne $true){throw 'Package validation failed'}
    }}else{Row 'release-package' 'SKIP' 'Requires both builds'}
    $null=Test-Case 'release-history' {Invoke-Child $ps @('-NoProfile','-ExecutionPolicy','Bypass','-File',(Join-Path $PSScriptRoot 'test-release-history.ps1'),'-OutputDirectory',(Join-Path $run 'history')) $root (Join-Path $run 'history')}
    $pageOk=Test-Case 'release-html-generation' {Invoke-Child $ps @('-NoProfile','-ExecutionPolicy','Bypass','-File',(Join-Path $PSScriptRoot 'create-release-page.ps1'),'-OutputPath',(Join-Path $run 'index.html'),'-Ga4IdFile',(Join-Path $run 'no-personal-ga4.txt')) $root (Join-Path $run 'html-generation')}
    $node=Get-Command node.exe -ErrorAction SilentlyContinue
    $browser=@('C:/Program Files/Google/Chrome/Application/chrome.exe','C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe') | Where-Object {Test-Path -LiteralPath $_}
    if($node -and $browser -and $pageOk){$null=Test-Case 'website-layout' {
        $e=Environment-For $run;$e['NICOBE_TEST_OUTPUT']=Join-Path $run 'website';$e['NICOBE_PAGE_PATH']=Join-Path $run 'index.html'
        Invoke-Child $node.Source @((Join-Path $PSScriptRoot 'check-page.cjs')) $root (Join-Path $run 'website') 120 $e
        if((Get-Content (Join-Path $run 'website/checks.json') -Raw -Encoding UTF8 | ConvertFrom-Json).passed -ne $true){throw 'Website checks failed'}
    }}else{Row 'website-layout' 'SKIP' 'Requires Node.js, Chrome/Edge and generated HTML'}
}catch{Row 'runner' 'FAIL' $_.Exception.ToString()}
finally{
    Row 'real-mpc-playback' 'SKIP' 'Manual: attach, file switch, seek/pause/speed, multiple players, exit and idle resource release. Existing players are never controlled.' 'Manual'
    Row 'visual-mixed-refresh' 'SKIP' 'Manual: actual display smoothness, mixed refresh rates, focus/Z-order and visual UI review. Offscreen tests cannot determine these.' 'Manual'
    Row 'live-api' 'SKIP' 'Manual: external API availability/rate limits; automated tests use mock HTTP only.' 'Manual'
    Row 'native-rebuild-release-zip' 'SKIP' 'Manual: MKV third-party source rebuild and final ZIP clean-machine installation. No release ZIP is generated here.' 'Manual'
    Save-Report
}
exit $resultCode
