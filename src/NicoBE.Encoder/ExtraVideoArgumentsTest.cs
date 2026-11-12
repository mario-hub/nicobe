using System.IO;
using System.Reflection;
using System.Text.Json;

namespace NicoBE.Encoder;

internal static class ExtraVideoArgumentsTest
{
    sealed class SilentProgress:IProgress<EncodeProgress>{public void Report(EncodeProgress value){}}
    internal static async Task<int> RunMedia(string input)
    {
        if(ReleaseTestEnvironment.Root==null)throw new Exception("Isolated release-test environment required.");
        string report=Path.Combine(EncoderPaths.Logs,"extra-arguments-media-test.json");var checks=new List<string>();
        try
        {
            var settings=EncoderSettings.Load();settings.Codec="H264";settings.Backend="CPU";settings.Passes=2;settings.RateControl="Bitrate";
            settings.Width=1280;settings.Height=720;settings.FrameRate=30;settings.EmbedComments=false;settings.SaveExternalAss=false;settings.UsePastLogComments=false;
            settings.ExtraVideoArgumentsEnabled=true;settings.ExtraVideoArguments="-preset ultrafast -b:v 1M -maxrate:v 3M -bufsize:v 6M -bf:v 2 -g:v 30";
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var result=await EncoderJob.Run(input,settings,new SilentProgress(),timeout.Token,3);
            var commands=File.ReadLines(result.Log).Where(l=>l.Contains(" Arguments: ")).Select(l=>JsonSerializer.Deserialize<string[]>(l[(l.IndexOf(" Arguments: ",StringComparison.Ordinal)+12)..])!).ToArray();
            if(commands.Length!=2 || commands.Any(a=>!a.Contains("-maxrate:v") || !a.Contains("-bf:v") || a.Contains("-preset")))throw new Exception("Both passes must receive normalized overrides.");
            checks.Add("real FFmpeg CPU two passes receive extra arguments");
            using var probe=await EncoderJob.Probe(settings.Ffprobe,result.Output,timeout.Token);
            var video=probe.RootElement.GetProperty("streams").EnumerateArray().First(s=>s.GetProperty("codec_type").GetString()=="video");
            if(video.GetProperty("codec_name").GetString()!="h264" || video.GetProperty("has_b_frames").GetInt32()!=2)throw new Exception("Output codec/B-frame verification failed.");
            checks.Add("H264 output contains requested B frames; encoding and sync verification succeeded");
            settings.Passes=1;settings.ExtraVideoArguments="-preset invalid_nicobe_preset";bool rejected=false;
            try{await EncoderJob.Run(input,settings,new SilentProgress(),timeout.Token,1);}
            catch(Exception ex){rejected=ex.Message.Contains("invalid_nicobe_preset",StringComparison.Ordinal);}
            if(!rejected)throw new Exception("FFmpeg error details must reach the caller.");checks.Add("invalid FFmpeg value reports diagnostic text");
            settings.ExtraVideoArguments="-x264-params aq-mode=invalid_nicobe_value";rejected=false;
            try{await EncoderJob.Run(input,settings,new SilentProgress(),timeout.Token,1);}
            catch(Exception ex){rejected=ex.Message.Contains("追加指定を適用できません",StringComparison.Ordinal);}
            if(!rejected)throw new Exception("An ignored encoder parameter must stop the encode.");checks.Add("ignored compound parameter stops encoding instead of silently succeeding");
            File.WriteAllText(report,JsonSerializer.Serialize(new{passed=true,checks},EncoderSettings.Json));return 0;
        }
        catch(Exception ex){File.WriteAllText(report,JsonSerializer.Serialize(new{passed=false,checks,error=ex.ToString()},EncoderSettings.Json));return 1;}
    }
    internal static int Run()
    {
        if(ReleaseTestEnvironment.Root==null)throw new Exception("Isolated release-test environment required.");
        var checks=new List<string>();
        void Check(bool ok,string name){if(!ok)throw new Exception(name);checks.Add(name);}
        void Reject(EncoderSettings s,string text){s.ExtraVideoArguments=text;bool failed=false;try{ExtraVideoArguments.Parse(s);}catch{failed=true;}Check(failed,"reject "+text);}
        string report=Path.Combine(EncoderPaths.Logs,"extra-arguments-test.json");
        try
        {
            var s=new EncoderSettings {Backend="CPU",Codec="H265",Passes=1,ExtraVideoArgumentsEnabled=true,ExtraVideoArguments="-preset fast\n-maxrate:v 6M -bufsize 12M -bf:v 3 -g 120 -x265-params \"pools=4:aq-mode=2\""};
            var args=EncoderJob.Arguments(s,"input.ts","output.mp4",0,new[]{1},null);
            string Value(List<string> a,string key)=>a[a.IndexOf(key)+1];
            Check(!args.Contains("-preset") && Value(args,"-preset:v")=="fast","preset replacement is unambiguous");
            Check(Value(args,"-x265-params:v")=="pools=4:frame-threads=2:aq-mode=2","compound merges keys and retains defaults");
            Check(Value(args,"-maxrate:v")=="6M" && Value(args,"-g:v")=="120","newline and alias normalization");
            Check(args.Contains("-copyts") && args.Contains("-vf") && Value(args,"-map")=="0:0" && args[^1]=="output.mp4","managed options unchanged");
            s.ExtraVideoArguments="-preset fast -preset:v slow -x265-params pools=2 -x265-params aq-mode=1";
            args=EncoderJob.Arguments(s,"input.ts","output.mp4",0,Array.Empty<int>(),null);
            Check(args.Count(x=>x=="-preset:v")==1 && Value(args,"-preset:v")=="slow","last scalar wins");
            Check(Value(args,"-x265-params:v")=="pools=2:frame-threads=2:aq-mode=1","repeated compound merges keys");
            foreach(string text in new[]{"-i other.ts","-map 0","-vf scale=16:16","-ss 10","-t 10","-r 24","-c:v copy","-pass 1","-progress out.txt","-metadata title=x","-preset fast other.mp4","-maxrate:a 6M","-preset \"fast","-bf","-x265-params stats=other","-x265-params fps=25","-x265-params pass=1","-x265-params csv=out.csv","-bf 2 & calc","-rc vbr"})Reject(s,text);
            s.Passes=2;Reject(s,"-crf 20");s.ExtraVideoArguments="-b:v 2M -bf 2";
            foreach(int pass in new[]{1,2})
            {
                args=EncoderJob.Arguments(s,"input.ts","out.mp4",0,new[]{1},null,pass,"stats");
                Check(Value(args,"-b:v")=="2M" && Value(args,"-bf:v")=="2" && Value(args,"-pass:v")==pass.ToString(),"both CPU passes receive override "+pass);
            }
            s.Backend="AMF";s.Passes=1;s.ExtraVideoArguments="-maxrate:v 9M";
            args=EncoderJob.Arguments(s,"input.ts","out.mp4",0,new[]{1},null);
            Check(!args.Contains("-maxrate") && Value(args,"-maxrate:v")=="9M","AMF generated maxrate replaced");
            s.Backend="NVENC";s.ExtraVideoArguments="-rc constqp -cq 24";Check(ExtraVideoArguments.Parse(s).Count==2,"NVENC options accepted");
            s.Ffmpeg=Path.Combine(EncoderPaths.Temp,"missing-ffmpeg.exe");
            bool refused=false;try{EncoderCapabilities.Resolve(s).GetAwaiter().GetResult();}catch(Exception ex){refused=ex.Message.Contains("自動変更しません");}
            Check(refused && s.Backend=="NVENC","GPU fallback refuses to reinterpret active arguments");
            s.ExtraVideoArgumentsEnabled=false;s.ExtraVideoArguments="-unsupported value";
            Check(ExtraVideoArguments.Parse(s).Count==0,"disabled text is retained but not evaluated");
            Check(EncoderCapabilities.Resolve(s).GetAwaiter().GetResult() && s.Backend=="CPU","normal GPU fallback preserved when disabled");
            s.ExtraVideoArgumentsEnabled=true;s.ExtraVideoArguments="-bf:v 3";
            string profile=Path.Combine(EncoderPaths.Temp,"extra-settings.json");s.Save(profile,false);var loaded=EncoderSettings.Load(profile);
            Check(loaded.ExtraVideoArgumentsEnabled && loaded.ExtraVideoArguments==s.ExtraVideoArguments,"JSON roundtrip");
            var window=new EncoderWindow(Array.Empty<string>(),loaded,()=>{});
            var panel=(ExtraVideoArgumentsPanel)typeof(EncoderWindow).GetField("extraArguments",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(window)!;
            Check(panel.Input.Text==s.ExtraVideoArguments && panel.Input.IsEnabled,"GUI loads settings");
            panel.EnabledSwitch.IsChecked=false;
            var saved=(EncoderSettings)typeof(EncoderWindow).GetMethod("ReadSettings",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(window,null)!;
            Check(!saved.ExtraVideoArgumentsEnabled && saved.ExtraVideoArguments==s.ExtraVideoArguments && !panel.Input.IsEnabled,"GUI disabling retains text for save");window.Close();
            Check(ExtraVideoArguments.Command("C:\\some dir\\ffmpeg.exe",new[]{"-i","a b.ts"})=="\"C:\\some dir\\ffmpeg.exe\" -i \"a b.ts\"","command display quotes spaces");
            File.WriteAllText(report,JsonSerializer.Serialize(new{passed=true,checks},EncoderSettings.Json));return 0;
        }
        catch(Exception ex){File.WriteAllText(report,JsonSerializer.Serialize(new{passed=false,checks,error=ex.ToString()},EncoderSettings.Json));return 1;}
    }
}
