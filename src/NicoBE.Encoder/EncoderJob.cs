using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace NicoBE.Encoder;

public sealed record EncodeProgress(string Stage,double Fraction,string Detail);
public sealed record EncodeResult(string Output,string Log,Mp4RecordingMetadata Metadata)
{
    public IReadOnlyList<string> Warnings {get;init;}=Array.Empty<string>();
}

public static class EncoderJob
{
    static string N(double value)=>value.ToString("0.#########",CultureInfo.InvariantCulture);
    static double Number(JsonElement e,string name,double fallback=0)=>e.TryGetProperty(name,out var value)
        && double.TryParse(value.ToString(),NumberStyles.Float,CultureInfo.InvariantCulture,out double number) && double.IsFinite(number)?number:fallback;
    static ProcessStartInfo StartInfo(string exe,IEnumerable<string> args)
    {
        var start=new ProcessStartInfo(exe) {UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=EncoderPaths.Root,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8};
        foreach(string arg in args)start.ArgumentList.Add(arg);
        start.Environment["TEMP"]=EncoderPaths.Temp;start.Environment["TMP"]=EncoderPaths.Temp;return start;
    }
    static void Kill(Process p){try{if(!p.HasExited)p.Kill(true);}catch(InvalidOperationException){}catch(System.ComponentModel.Win32Exception){}}
    internal static async Task<string> Capture(string exe,IEnumerable<string> args,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();using var p=Process.Start(StartInfo(exe,args))??throw new Exception("ツールを起動できません。");
        using var cancel=token.Register(()=>Kill(p));var output=p.StandardOutput.ReadToEndAsync();var error=p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();string text=await output,errors=await error;token.ThrowIfCancellationRequested();
        if(p.ExitCode!=0)throw new Exception("動画情報を取得できません: "+errors[^Math.Min(errors.Length,1600)..]);return text;
    }
    internal static async Task<JsonDocument> Probe(string exe,string file,CancellationToken token)=>JsonDocument.Parse(await Capture(exe,
        new[]{"-v","error","-probesize","32000000","-analyzeduration","10000000","-show_programs","-show_streams","-show_format","-of","json",file},token));
    static async Task<double> FirstFrame(string exe,string file,int video,CancellationToken token)
    {
        using var json=JsonDocument.Parse(await Capture(exe,new[]{"-v","error","-probesize","32000000","-analyzeduration","10000000","-read_intervals","%+5","-select_streams",video.ToString(CultureInfo.InvariantCulture),"-show_frames","-show_entries","frame=best_effort_timestamp_time","-of","json",file},token));
        foreach(var frame in json.RootElement.GetProperty("frames").EnumerateArray())
            if(frame.TryGetProperty("best_effort_timestamp_time",out var pts) && double.TryParse(pts.ToString(),NumberStyles.Float,CultureInfo.InvariantCulture,out double value) && double.IsFinite(value))return value;
        throw new Exception("先頭の映像時刻を取得できないため、同期情報を保存できません。");
    }
    public static string EncoderName(EncoderSettings s)=>((s.Codec switch {"H264"=>"h264","H265"=>"hevc","AV1"=>"av1",_=>throw new Exception("映像形式が不明です。")})+s.Backend) switch
    {
        "h264CPU"=>"libx264","hevcCPU"=>"libx265","h264NVENC"=>"h264_nvenc","hevcNVENC"=>"hevc_nvenc",
        "h264QSV"=>"h264_qsv","hevcQSV"=>"hevc_qsv","h264AMF"=>"h264_amf","hevcAMF"=>"hevc_amf",
        "av1CPU"=>"libaom-av1","av1NVENC"=>"av1_nvenc","av1QSV"=>"av1_qsv","av1AMF"=>"av1_amf",_=>throw new Exception("エンコーダーが不明です。")
    };
    internal static List<string> Arguments(EncoderSettings s,string input,string output,int video,IReadOnlyList<int> audio,double? limit,int pass=0,string? stats=null,IReadOnlyDictionary<int,bool>? dualMono=null)
    {
        dualMono ??= new Dictionary<int,bool>();
        var args=new List<string>{"-hide_banner","-nostdin","-n","-loglevel","warning","-copyts","-start_at_zero"};
        void Input(bool english)
        {
            args.AddRange(new[]{"-probesize","32000000","-analyzeduration","10000000"});
            foreach(var track in dualMono) args.AddRange(new[]{"-dual_mono_mode:"+track.Key,track.Value != english ? "main":"sub"});
            // Align both decoder inputs to the selected video/audio timeline.
            if(english)args.AddRange(new[]{"-isync","0"});
            args.AddRange(new[]{"-i",input});
        }
        Input(false);if(dualMono.Count>0)Input(true);
        args.AddRange(new[]{"-map","0:"+video});
        foreach(int index in audio)
        {
            args.AddRange(new[]{"-map","0:"+index});
            if(dualMono.ContainsKey(index))args.AddRange(new[]{"-map","1:"+index});
        }
        args.AddRange(new[]{"-map_metadata","-1","-map_chapters","-1","-sn","-dn","-c:v",EncoderName(s)});
        string[] presets=s.Backend=="NVENC"?new[]{"p2","p4","p6"}:s.Backend=="AMF"?new[]{"speed","balanced","quality"}:new[]{"fast","medium","slow"};
        if(s.Backend=="CPU" && s.Codec=="AV1")
            args.AddRange(new[]{"-cpu-used",s.Speed=="Fast"?"8":s.Speed=="Quality"?"4":"6","-row-mt","1"});
        else args.AddRange(new[]{s.Backend=="AMF"?"-quality":"-preset",presets[s.Speed=="Fast"?0:s.Speed=="Quality"?2:1]});
        if(s.Backend=="NVENC")args.AddRange(new[]{"-multipass",s.Passes==2?"fullres":"disabled"});
        if(pass>0)
        {
            args.AddRange(new[]{"-pass:v",pass.ToString(CultureInfo.InvariantCulture)});
            args.AddRange(new[]{"-passlogfile:v",stats??throw new ArgumentNullException(nameof(stats))});
            if(s.Codec=="H265")args.AddRange(new[]{"-x265-stats",stats});
        }
        if(s.RateControl=="Bitrate")
        {
            if(s.Backend=="NVENC")args.AddRange(new[]{"-rc","vbr"});
            if(s.Backend=="AMF")args.AddRange(new[]{"-rc","vbr_peak","-maxrate",N(s.BitrateMbps*1.5)+"M"});
            args.AddRange(new[]{"-b:v",N(s.BitrateMbps)+"M"});
        }
        else switch(s.Backend)
        {
            case "CPU":args.AddRange(new[]{"-crf",N(s.Quality)});if(s.Codec=="AV1")args.AddRange(new[]{"-b:v","0"});break;
            case "NVENC":args.AddRange(new[]{"-rc","vbr","-cq",N(s.Quality),"-b:v","0"});break;
            case "QSV":args.AddRange(new[]{"-global_quality",N(s.Quality)});break;
            case "AMF":args.AddRange(new[]{"-rc","cqp","-qp_i",N(s.Quality),"-qp_p",N(s.Quality)});break;
        }
        args.AddRange(new[]{"-threads:v","8"});
        if(s.Backend=="CPU" && s.Codec=="H265")args.AddRange(new[]{"-x265-params","pools=8:frame-threads=2"});
        string filter=s.Deinterlace=="Off"?"":$"bwdif=mode={(s.FrameRate==60?"send_field":"send_frame")}:parity=auto:deint={(s.Deinterlace=="On"?"all":"interlaced")},";
        // Resample on the source timeline. Output CFR sync would pad frames back
        // to zero and invalidate the first-frame correspondence used for comments.
        filter+=$"scale={s.Width}:{s.Height}:force_original_aspect_ratio=decrease:force_divisible_by=2:reset_sar=1,pad={s.Width}:{s.Height}:(ow-iw)/2:(oh-ih)/2,setsar=1,fps=fps={s.FrameRate}:round=near";
        args.AddRange(new[]{"-vf",filter,"-pix_fmt","yuv420p","-fps_mode","passthrough"});
        if(s.Codec=="H265" && s.Container!="MKV")args.AddRange(new[]{"-tag:v","hvc1"});
        if(audio.Count>0){args.AddRange(new[]{"-c:a",s.Audio=="Copy"?"copy":"aac"});if(s.Audio!="Copy")args.AddRange(new[]{"-b:a",s.Audio[3..]+"k"});}
        if(dualMono.Count>0)
        {
            int outputIndex=0;bool defaultSet=false;
            foreach(int index in audio)
            {
                if(dualMono.ContainsKey(index))
                    foreach(string language in new[]{"jpn","eng"})
                    {
                        string title=language=="jpn"?"日本語":"英語";
                        args.AddRange(new[]{"-c:a:"+outputIndex,"aac","-b:a:"+outputIndex,(s.Audio=="Copy"?"128":s.Audio[3..])+"k",
                            "-metadata:s:a:"+outputIndex,"language="+language,"-metadata:s:a:"+outputIndex,"title="+title,
                            "-metadata:s:a:"+outputIndex,"handler_name="+title,"-disposition:a:"+outputIndex,!defaultSet && language=="jpn"?"default":"0"});
                        if(language=="jpn")defaultSet=true;
                        outputIndex++;
                    }
                else {args.AddRange(new[]{"-disposition:a:"+outputIndex,"0"});outputIndex++;}
            }
        }
        if(limit.HasValue)args.AddRange(new[]{"-t",N(limit.Value)});
        args.AddRange(new[]{"-progress","pipe:1","-stats_period","0.5","-nostats"});
        // Keep the selected audio streams in pass 1 too: start_at_zero must use
        // the same stream start times and fps sampling grid in both passes.
        if(pass==1)args.AddRange(new[]{"-f","null","-"});
        else
        {
            if(s.Container!="MKV")args.AddRange(new[]{"-movflags","+faststart+use_metadata_tags"});
            args.AddRange(new[]{"-metadata","nicobe_encoder=1",output});
        }
        return args;
    }
    internal static async Task RunPass(EncoderSettings settings,List<string> args,string log,double duration,IProgress<EncodeProgress> progress,CancellationToken token,int index,int count,Func<TextWriter>? openLog=null)
    {
        string stage=count==2?$"{index}/2パス":settings.Backend=="NVENC" && settings.Passes==2?"エンコード中（NVENC 2パス）":"エンコード中";
        progress.Report(new(stage,(double)(index-1)/count,"処理を開始しています"));
        await File.AppendAllTextAsync(log,$"\n{stage} Arguments: "+JsonSerializer.Serialize(args)+"\n",token);
        token.ThrowIfCancellationRequested();
        using var p=Process.Start(StartInfo(settings.Ffmpeg,args))??throw new Exception("FFmpegを起動できません。");
        using var cancel=token.Register(()=>Kill(p));
        var errors=Task.Run(async()=>
        {
            try
            {
                await using var writer=openLog?.Invoke()??new StreamWriter(log,true,Encoding.UTF8);
                while(await p.StandardError.ReadLineAsync() is {} line)await writer.WriteLineAsync(line);
            }
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
            {throw new IOException("処理ログの読み書きに失敗したため、エンコードを停止しました。\nログ: "+log,ex);}
        });
        // Stop the producer if stderr can no longer be drained. Otherwise its
        // full pipe can deadlock both FFmpeg and our stdout/exit wait.
        _=errors.ContinueWith(_=>Kill(p),CancellationToken.None,TaskContinuationOptions.OnlyOnFaulted|TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
        try
        {
        double fraction=0;string speed="";
        while(await p.StandardOutput.ReadLineAsync() is {} line)
        {
            int split=line.IndexOf('=');if(split<0)continue;string key=line[..split],value=line[(split+1)..];
            if(key=="out_time_us" && double.TryParse(value,NumberStyles.Float,CultureInfo.InvariantCulture,out double micros))fraction=Math.Clamp(micros/1000000/Math.Max(1,duration),0,1);
            if(key=="speed")speed=value;
            if(key=="progress")progress.Report(new(stage,(index-1+fraction)/count,$"{fraction:P0}　速度 {speed}"));
        }
        await p.WaitForExitAsync();await errors;token.ThrowIfCancellationRequested();
        if(p.ExitCode!=0)throw new Exception($"{stage}に失敗しました（終了コード {p.ExitCode}）。\nログ: {log}");
        }
        finally
        {
            Kill(p);
            await p.WaitForExitAsync();
            try{await errors;}catch(Exception){} // Observe faults even when stdout/progress failed first.
        }
    }
    public static string ResolveOutputPath(string input,EncoderSettings settings,string? outputFile=null)
    {
        string extension=settings.Container=="MKV"?".mkv":".mp4";
        bool Occupied(string path)=>File.Exists(path)||Directory.Exists(path)
            || File.Exists(Path.ChangeExtension(path,".ass"))||Directory.Exists(Path.ChangeExtension(path,".ass"));
        if(outputFile!=null)
        {
            string path=Path.GetFullPath(outputFile);
            if(!Path.GetExtension(path).Equals(extension,StringComparison.OrdinalIgnoreCase))throw new ArgumentException("出力ファイルの拡張子と保存形式が一致しません。");
            if(Occupied(path))throw new IOException("出力動画または同名ASSが既に存在します: "+path);
            return path;
        }
        string stem=Path.GetFileNameWithoutExtension(input);
        string final=Path.Combine(Path.GetFullPath(settings.OutputFolder),stem+extension);
        for(int i=2;Occupied(final);i++)final=Path.Combine(Path.GetFullPath(settings.OutputFolder),stem+$" ({i})"+extension);
        return final;
    }
    public static async Task<EncodeResult> Run(string input,EncoderSettings settings,IProgress<EncodeProgress> progress,CancellationToken token,double? testSeconds=null,string? outputFile=null)
    {
        string requestedBackend=settings.Backend;
        bool cpuFallback=await EncoderCapabilities.Resolve(settings,token);
        settings.Validate();input=Path.GetFullPath(input);
        if(!File.Exists(input)||!new[]{".ts",".m2ts",".mts"}.Contains(Path.GetExtension(input).ToLowerInvariant()))throw new Exception("TSファイルを選択してください。");
        string final=ResolveOutputPath(input,settings,outputFile);
        string outputFolder=Path.GetDirectoryName(final)!;Directory.CreateDirectory(outputFolder);
        string stamp=DateTime.Now.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N")[..8];
        string log=Path.Combine(EncoderPaths.Logs,stamp+".log");
        string extension=settings.Container=="MKV"?".mkv":".mp4";
        string partial=Path.Combine(outputFolder,stamp+".partial"+extension);
        string stem=Path.GetFileNameWithoutExtension(final);
        string partialAss=Path.ChangeExtension(partial,".ass"),finalAss=Path.ChangeExtension(final,".ass");
        bool videoPublished=false,assPublished=false;
        bool encodedSuccessfully=false;
        var warnings=new List<string>();
        if(cpuFallback)warnings.Add(requestedBackend+"を初期化できないため、CPUでエンコードしました。");
        string? passDirectory=null;
        try
        {
            progress.Report(new("録画情報を取得",0,Path.GetFileName(input)));
            var recording=await Task.Run(()=>TsReader.Read(input,null,token),token);
            if(recording.NetworkId<=0)throw new Exception("放送局の識別情報を取得できません。");
            using var probe=await Probe(settings.Ffprobe,input,token);
            var program=probe.RootElement.GetProperty("programs").EnumerateArray().FirstOrDefault(p=>Number(p,"program_id")==recording.ServiceId);
            if(program.ValueKind==JsonValueKind.Undefined)throw new Exception("TSの映像サービスとエンコーダーの情報が一致しません。");
            var streams=program.GetProperty("streams").EnumerateArray().ToArray();
            int video=streams.First(s=>s.TryGetProperty("codec_type",out var type) && type.GetString()=="video").GetProperty("index").GetInt32();
            var audio=streams.Where(s=>s.TryGetProperty("codec_type",out var type) && type.GetString()=="audio").Select(s=>s.GetProperty("index").GetInt32()).ToArray();
            double duration=testSeconds??Number(probe.RootElement.GetProperty("format"),"duration",(recording.End-recording.Start).TotalSeconds);
            progress.Report(new("音声を確認",0,"日本語・英語の二重音声を確認しています"));
            var dualMono=await Task.Run(()=>DualMonoAudio.Detect(input,recording with {End=recording.Start.AddSeconds(duration)},streams,token),token);
            PreparedComments? comments=null;
            if(settings.EmbedComments || settings.SaveExternalAss)
            {
                progress.Report(new("コメントを準備",0,"NicoJKログを読み込んでいます"));
                var commentConfig=Config.Load(false);commentConfig.NicoJkLogFolder=settings.NicoJkLogFolder;
                try
                {
                    comments=await Task.Run(()=>LogReader.Prepare(recording with {End=recording.Start.AddSeconds(duration+5)},commentConfig,token,settings.Container=="MP4" && settings.EmbedComments),token);
                    if(comments.Malformed>0)warnings.Add($"読み取れないコメントが {comments.Malformed} 件あり、保存対象から除外しました。");
                    if((comments.SourceComments?.Count??comments.Comments.Count)==0)
                    {warnings.Add("対象コメントが0件のため、コメントを保存せずに動画を出力しました。");comments=null;}
                }
                catch(CommentLogUnavailableException ex)
                {warnings.Add("対応するコメントログがないため、コメントを保存せずに動画を出力しました。\n"+ex.Message);}
            }
            double sourcePts=await FirstFrame(settings.Ffprobe,input,video,token);
            const double wrap=8589934592d/90000;
            double delta=sourcePts-recording.FirstPcrSeconds;delta-=Math.Round(delta/wrap)*wrap;
            if(Math.Abs(delta)>60)throw new Exception("TSの放送時刻と映像時刻の差が大きいため、同期情報を安全に作成できません。");
            await File.WriteAllTextAsync(log,"Input: "+input+"\nSettings: "+JsonSerializer.Serialize(settings)+"\n",token);
            await File.AppendAllTextAsync(log,"Dual mono (stream: Japanese is main): "+JsonSerializer.Serialize(dualMono)+"\n",token);
            bool fullTwoPass=settings.Backend=="CPU" && settings.Passes==2;
            string? stats=null;
            if(fullTwoPass)
            {
                passDirectory=Path.Combine(EncoderPaths.Temp,"passes-"+stamp);Directory.CreateDirectory(passDirectory);stats=Path.Combine(passDirectory,"stats");
                await RunPass(settings,Arguments(settings,input,partial,video,audio,testSeconds,1,stats,dualMono),log,duration,progress,token,1,2);
            }
            await RunPass(settings,Arguments(settings,input,partial,video,audio,testSeconds,fullTwoPass?2:0,stats,dualMono),log,duration,progress,token,fullTwoPass?2:1,fullTwoPass?2:1);
            encodedSuccessfully=true;
            progress.Report(new("同期情報を保存",1,"出力動画を確認しています"));
            using var encoded=await Probe(settings.Ffprobe,partial,token);
            ValidateAudio(encoded.RootElement,audio,dualMono);
            var outputVideo=encoded.RootElement.GetProperty("streams").EnumerateArray().First(s=>s.GetProperty("codec_type").GetString()=="video");
            if(outputVideo.GetProperty("codec_name").GetString()!=(settings.Codec switch {"H264"=>"h264","H265"=>"hevc",_=>"av1"}))throw new Exception("出力映像形式が設定と一致しません。");
            if(Number(outputVideo,"width")!=settings.Width || Number(outputVideo,"height")!=settings.Height)throw new Exception("出力解像度が設定と一致しません。");
            var fps=outputVideo.GetProperty("avg_frame_rate").GetString()!.Split('/');
            if(fps.Length!=2 || !double.TryParse(fps[0],CultureInfo.InvariantCulture,out double numerator) || !double.TryParse(fps[1],CultureInfo.InvariantCulture,out double denominator)
                || denominator<=0 || Math.Abs(numerator/denominator-settings.FrameRate)>.01)throw new Exception("出力フレームレートが設定と一致しません。");
            double outputPts=await FirstFrame(settings.Ffprobe,partial,outputVideo.GetProperty("index").GetInt32(),token);
            // start_at_zero uses the effective start of the selected streams.
            // The container's start can belong to a different service in a TS.
            double selectedStart=streams.Where(s=>s.GetProperty("index").GetInt32()==video || audio.Contains(s.GetProperty("index").GetInt32()))
                .Select(s=>Number(s,"start_time",double.NaN)).Where(double.IsFinite).DefaultIfEmpty(Number(probe.RootElement.GetProperty("format"),"start_time")).Min();
            double expectedFirst=sourcePts-selectedStart;
            await File.AppendAllTextAsync(log,$"\nTiming: source={N(sourcePts)} selectedStart={N(selectedStart)} expected={N(expectedFirst)} actual={N(outputPts)}\n",token);
            if(Math.Abs(outputPts-expectedFirst)>1d/settings.FrameRate+.02)throw new Exception("先頭映像の時刻が想定より変化したため、コメント同期を確認できません。");
            double outputDuration=Number(encoded.RootElement.GetProperty("format"),"duration");
            var metadata=new Mp4RecordingMetadata(1,recording.Start.AddSeconds(delta-outputPts).ToUniversalTime(),recording.Start.ToUniversalTime(),recording.ServiceId,recording.NetworkId,Path.GetFileName(input),outputDuration,sourcePts,outputPts);
            if(comments!=null)
            {
                double offset=(recording.Start-metadata.PlaybackZeroUtc).TotalSeconds;
                var inRange=comments.Comments.Where(c=>c.MediaSeconds+offset>=0 && c.MediaSeconds+offset<outputDuration-.01).ToList();
                var rawInRange=comments.SourceComments?.Where(c=>c.MediaSeconds+offset>=0 && c.MediaSeconds+offset<outputDuration).ToList();
                if((rawInRange?.Count??inRange.Count)==0){warnings.Add("出力動画の時間内にコメントがないため、コメントを保存せずに動画を出力しました。");comments=null;}
                else comments=comments with {Comments=inRange,SourceComments=rawInRange};
            }
            if(settings.Container=="MKV")
            {
                var selected=(settings.EmbedComments?comments:null)?.Comments.Select(c=>c with {MediaSeconds=c.MediaSeconds+(recording.Start-metadata.PlaybackZeroUtc).TotalSeconds})
                    .Where(c=>c.MediaSeconds>=0 && c.MediaSeconds<outputDuration).ToList();
                progress.Report(new("字幕・同期情報を保存",1,selected==null?"MKVへ録画情報を保存しています":"MKVへASS字幕を埋め込んでいます"));
                await FinishMkv(partial,settings,metadata,selected,audio,dualMono,warnings,token);
            }
            else
            {
                metadata.Append(partial);var check=Mp4RecordingMetadata.Read(partial,token);
                if(check!=metadata)throw new Exception("同期メタデータの書き込み検証に失敗しました。");
            }
            if(settings.EmbedComments && comments!=null && settings.Container=="MP4")
            {
                progress.Report(new("コメントを埋め込み",1,"MP4へ保存して読み戻しています"));
                double offset=(recording.Start-metadata.PlaybackZeroUtc).TotalSeconds;
                var selected=comments.SourceComments!.Select(c=>c with {MediaSeconds=c.MediaSeconds+offset}).ToList();
                var embedded=new Mp4CommentData(1,comments.Channel,selected);embedded.Append(partial,token);
                var restored=Mp4CommentData.TryRead(partial,token);
                if(restored==null || restored.Channel!=embedded.Channel || !embedded.ContentEquals(restored))throw new Exception("埋め込みコメントの検証に失敗しました。");
                await File.AppendAllTextAsync(log,$"\nEmbedded comments: {selected.Count}\n",token);
            }
            if(comments is {Comments.Count:0} && settings.SaveExternalAss)warnings.Add("表示対象のコメントがないため、外部ASSファイルは作成しませんでした。MP4内の元コメントデータは保持しています。");
            if(comments is {Comments.Count:>0} && settings.SaveExternalAss)
            {
                progress.Report(new("ASSを保存",1,"動画と同名の外部ASSを作成しています"));
                double offset=(recording.Start-metadata.PlaybackZeroUtc).TotalSeconds;
                var selected=comments.Comments.Select(c=>c with {MediaSeconds=c.MediaSeconds+offset})
                    .Where(c=>c.MediaSeconds>=0 && c.MediaSeconds<outputDuration).ToList();
                int written=AssComments.Write(partialAss,selected,token,outputDuration,settings.CommentSizePercent);
                var restored=AssComments.ReadFile(partialAss,token);
                if(written>0 && restored.Count==0)throw new IOException("外部ASSコメントの読み戻しに失敗しました。");
                if(written==0){File.Delete(partialAss);warnings.Add("配置できるコメントがないため、外部ASSファイルは作成しませんでした。");}
            }
            token.ThrowIfCancellationRequested();File.Move(partial,final);videoPublished=true;
            if(File.Exists(partialAss)){File.Move(partialAss,finalAss);assPublished=true;}
            await File.AppendAllTextAsync(log,"\nCompleted: "+final+"\nMetadata: "+JsonSerializer.Serialize(metadata)+"\n",CancellationToken.None);
            if(assPublished)await File.AppendAllTextAsync(log,"ASS: "+finalAss+"\n",CancellationToken.None);
            if(warnings.Count>0)await File.AppendAllTextAsync(log,"Warnings:\n"+string.Join("\n",warnings)+"\n",CancellationToken.None);
            progress.Report(new("完了",1,Path.GetFileName(final)));return new(final,log,metadata) {Warnings=warnings.AsReadOnly()};
        }
        catch(Exception ex)
        {
            string retained="";
            if(File.Exists(partial))
            {
                retained=partial;
                string preserved=Path.Combine(outputFolder,stem+(encodedSuccessfully?"_同期確認失敗_":"_未完了_")+stamp+extension);
                try{File.Move(partial,preserved);retained=preserved;}catch(IOException){}catch(UnauthorizedAccessException){}
            }
            else if(videoPublished && File.Exists(final))retained=final;
            string message=ex.Message+(retained.Length>0?"\n動画は削除せず保持しました: "+retained:"\n動画ファイルはまだ生成されていません。")+"\n処理ログ: "+log;
            if(assPublished)message+="\nASS: "+finalAss;
            else if(File.Exists(partialAss))message+="\n未完了のASS: "+partialAss;
            try{await File.AppendAllTextAsync(log,"\n"+ex+"\n"+message+"\n",CancellationToken.None);}catch(IOException){}catch(UnauthorizedAccessException){}
            if(ex is OperationCanceledException)throw new OperationCanceledException(message,ex,token);
            throw new Exception(message,ex);
        }
        finally
        {
            if(passDirectory!=null && Path.GetFullPath(passDirectory).StartsWith(Path.GetFullPath(EncoderPaths.Temp)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
            {
                try{Directory.Delete(passDirectory,true);}catch(IOException){}catch(UnauthorizedAccessException){}
            }
        }
    }

    internal static void ValidateAudio(JsonElement probe,IReadOnlyList<int> input,IReadOnlyDictionary<int,bool> dualMono)
    {
        var audio=probe.GetProperty("streams").EnumerateArray().Where(s=>s.TryGetProperty("codec_type",out var type) && type.GetString()=="audio").ToArray();
        if(audio.Length!=input.Count+dualMono.Count)throw new IOException("出力音声トラック数が一致しません。");
        int output=0;bool defaultSet=false;
        foreach(int index in input)
        {
            if(!dualMono.ContainsKey(index)){output++;continue;}
            foreach(string language in new[]{"jpn","eng"})
            {
                var stream=audio[output++];
                bool isDefault=stream.GetProperty("disposition").GetProperty("default").GetInt32()!=0;
                if(stream.GetProperty("codec_name").GetString()!="aac" || !stream.TryGetProperty("tags",out var tags)
                    || !tags.TryGetProperty("language",out var tag) || tag.GetString()!=language
                    || isDefault!=(!defaultSet && language=="jpn"))throw new IOException("二重音声の言語・既定トラックを確認できません。");
                if(language=="jpn")defaultSet=true;
            }
        }
    }
    static async Task FinishMkv(string partial,EncoderSettings settings,Mp4RecordingMetadata metadata,List<Comment>? comments,IReadOnlyList<int> audio,IReadOnlyDictionary<int,bool> dualMono,List<string> warnings,CancellationToken token)
    {
        string ass=Path.Combine(EncoderPaths.Temp,"comments-"+Guid.NewGuid().ToString("N")+".ass");
        string muxed=partial+".mux.mkv";
        try
        {
            var args=new List<string>{"-v","error","-nostdin","-n","-copyts","-i",partial};
            bool hasAss=false;
            if(comments!=null)
            {
                hasAss=AssComments.Write(ass,comments,token,metadata.DurationSeconds,settings.CommentSizePercent)>0;
                if(hasAss)args.AddRange(new[]{"-i",ass});
                else warnings.Add("配置できるコメントがないため、MKVにASS字幕を格納しませんでした。");
            }
            args.AddRange(new[]{"-map","0","-map_metadata","-1","-c","copy","-avoid_negative_ts","disabled"});
            if(hasAss)args.AddRange(new[]{"-map","1:0","-c:s","ass","-metadata:s:s:0","title="+ContainerComments.TrackTitle,"-metadata:s:s:0","language=jpn","-disposition:s:0","0"});
            args.AddRange(new[]{"-metadata",ContainerComments.MetadataTag+"="+JsonSerializer.Serialize(metadata),muxed});
            await Capture(settings.Ffmpeg,args,token);
            using var probe=await Probe(settings.Ffprobe,muxed,token);
            ValidateAudio(probe.RootElement,audio,dualMono);
            if(ContainerComments.Metadata(probe.RootElement.GetProperty("format"))!=metadata)throw new IOException("MKVの録画情報を確認できません。");
            int video=probe.RootElement.GetProperty("streams").EnumerateArray().First(s=>s.GetProperty("codec_type").GetString()=="video").GetProperty("index").GetInt32();
            double first=await FirstFrame(settings.Ffprobe,muxed,video,token);
            if(Math.Abs(first-metadata.OutputFirstVideoPts)>.002)throw new IOException("MKV字幕追加後の映像時刻が変化しました。");
            if(hasAss)
            {
                string restored=await Capture(settings.Ffmpeg,new[]{"-v","error","-nostdin","-copyts","-i",muxed,"-map","0:s:0","-f","ass","pipe:1"},token);
                var expected=AssComments.ReadFile(ass,token);
                var actual=AssComments.Read(restored,token);
                if(expected.Count!=actual.Count || expected.Where((c,i)=>c.Text!=actual[i].Text || Math.Abs(c.MediaSeconds-actual[i].MediaSeconds)>.011).Any())
                    throw new IOException("MKV内のASSコメントを確認できません。");
            }
            token.ThrowIfCancellationRequested();File.Move(muxed,partial,true);
        }
        finally
        {
            foreach(string file in new[]{ass,muxed})
                try{if(File.Exists(file))File.Delete(file);}catch(IOException){}catch(UnauthorizedAccessException){}
        }
    }
}
