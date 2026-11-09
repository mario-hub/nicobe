using System.IO;
using System.Text.Json;

namespace NicoBE.Encoder;

public static class SmokeTest
{
    public static async Task<int> Formats(string input,bool sidecarOnly=false)
    {
        var results=new List<object>();
        try
        {
            string legacy=Path.Combine(EncoderPaths.Root,"temp", "test-results","legacy-sidecar-settings.json");
            File.WriteAllText(legacy,"{\"Container\":\"MP4+ASS\",\"EmbedComments\":true,\"CommentSizePercent\":150}");
            var migrated=EncoderSettings.Load(legacy);
            if(migrated.Container!="MP4" || migrated.EmbedComments || !migrated.SaveExternalAss || migrated.CommentSizePercent!=150)throw new Exception("Legacy sidecar settings migration");
            foreach(string container in new[]{"MP4","MKV"})
            foreach(bool embed in new[]{false,true})
            foreach(bool external in sidecarOnly?new[]{true}:new[]{false,true})
            {
                var settings=EncoderSettings.Load();settings.Container=container;settings.EmbedComments=embed;
                settings.SaveExternalAss=external;
                settings.CommentSizePercent=150;
                settings.Backend="CPU";settings.Codec="H264";settings.Passes=1;settings.RateControl="Quality";
                settings.Speed="Fast";settings.Width=1280;settings.Height=720;settings.FrameRate=30;
                settings.OutputFolder=Path.Combine(EncoderPaths.Root,"temp", "test-results","formats");
                using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(2));
                var output=await EncoderJob.Run(input,settings,new SilentProgress(),timeout.Token,3);
                var config=Config.Load(false);config.CommentPreference="Original";
                if(embed || external)config.NicoJkLogFolder=Path.Combine(EncoderPaths.Temp,"no-external-logs");
                var loaded=LogReader.Prepare(output.Output,config,timeout.Token);
                if(loaded.Comments.Count==0)throw new Exception("No comments from "+container);
                if(container=="MP4" && (Mp4CommentData.TryRead(output.Output,timeout.Token)!=null)!=embed)throw new Exception("MP4 embedding flag");
                var original=container=="MP4"?Mp4CommentData.TryRead(output.Output,timeout.Token):null;
                if(original!=null && original.ForPlayback().Any(c=>!new[]{.8,1,1.3}.Any(size=>Math.Abs(size-c.SizeScale)<.001)))throw new Exception("MP4 original size must not be scaled");
                if(container=="MKV")
                {
                    using var probe=await EncoderJob.Probe(settings.Ffprobe,output.Output,timeout.Token);
                    if(probe.RootElement.GetProperty("streams").EnumerateArray().Any(s=>s.GetProperty("codec_type").GetString()=="subtitle")!=embed)throw new Exception("MKV embedding flag");
                    if(embed)
                    {
                        string ass=await EncoderJob.Capture(settings.Ffmpeg,new[]{"-v","error","-nostdin","-i",output.Output,"-map","0:s:0","-f","ass","pipe:1"},timeout.Token);
                        var embedded=AssComments.Read(ass,timeout.Token);
                        if(embedded.Count==0 || embedded.Any(c=>!new[]{1.2,1.5,1.95}.Any(size=>Math.Abs(size-c.SizeScale)<.001)))throw new Exception("Embedded ASS size");
                    }
                }
                string externalFile=Path.ChangeExtension(output.Output,".ass");
                if(File.Exists(externalFile)!=external)throw new Exception("External ASS output flag");
                if(external)
                {
                    var ass=AssComments.ReadFile(externalFile,timeout.Token);
                    if(ass.Count==0 || ass.Any(c=>!new[]{1.2,1.5,1.95}.Any(size=>Math.Abs(size-c.SizeScale)<.001)))throw new Exception("External ASS size");
                }
                if(container=="MP4" && embed && !external)
                {
                    string sidecar=Path.ChangeExtension(output.Output,null)+"-nicojk-1080S.ass";
                    AssComments.Write(sidecar,new[]{new Comment(1,"外部ASS優先確認","#FFFFFF",1,CommentPosition.Scroll)},timeout.Token);
                    config.CommentPreference="ASS";
                    if(LogReader.Prepare(output.Output,config,timeout.Token).Comments.Single().Text!="外部ASS優先確認")throw new Exception("ASS preference");
                    config.CommentPreference="Original";
                    if(LogReader.Prepare(output.Output,config,timeout.Token).Channel=="ASS")throw new Exception("Original preference");
                }
                string serialized=JsonSerializer.Serialize(settings,EncoderSettings.Json);
                var saved=JsonSerializer.Deserialize<EncoderSettings>(serialized)!;
                if(saved.Container!=container || saved.EmbedComments!=embed || saved.SaveExternalAss!=external)throw new Exception("Format settings round-trip");
                results.Add(new{container,embed,external,count=loaded.Comments.Count,output=output.Output});
            }
            File.WriteAllText(Path.Combine(EncoderPaths.Root,"temp", "test-results",sidecarOnly?"sidecar-test.json":"format-test.json"),JsonSerializer.Serialize(new{passed=true,results},EncoderSettings.Json));return 0;
        }
        catch(Exception ex)
        {
            File.WriteAllText(Path.Combine(EncoderPaths.Root,"temp", "test-results",sidecarOnly?"sidecar-test.json":"format-test.json"),JsonSerializer.Serialize(new{passed=false,results,error=ex.ToString()},EncoderSettings.Json));return 1;
        }
    }
    public static async Task<int> Recover(string input)
    {
        try
        {
            var settings=EncoderSettings.Load();
            var result=await EncoderJob.Run(input,settings,new SilentProgress(),CancellationToken.None);
            File.WriteAllText(Path.Combine(EncoderPaths.Root,"temp", "test-results","encoder-recovery.json"),JsonSerializer.Serialize(new{passed=true,result},EncoderSettings.Json));return 0;
        }
        catch(Exception ex){File.WriteAllText(Path.Combine(EncoderPaths.Root,"temp", "test-results","encoder-recovery.json"),JsonSerializer.Serialize(new{passed=false,error=ex.ToString()},EncoderSettings.Json));return 1;}
    }
    sealed class SilentProgress : IProgress<EncodeProgress>{public void Report(EncodeProgress value){}}
    public static async Task<int> Run(string input,bool twoPass=false)
    {
        var results=new List<object>();
        try
        {
            // Short real encodes only. Never overwrite the user's saved encoder settings.
            var cases=twoPass?new[]{("H264","CPU",1280,720,24),("H265","CPU",1280,720,30),("H265","NVENC",1600,900,60)}:new[]{("H264","CPU",1280,720,30),("H265","NVENC",1600,900,60)};
            foreach(var test in cases)
            {
                var s=new EncoderSettings {Codec=test.Item1,Backend=test.Item2,Width=test.Item3,Height=test.Item4,Speed="Fast",FrameRate=test.Item5,
                    OutputFolder=Path.Combine(EncoderPaths.Root,"temp", "test-results","encoder-custom-output"),EmbedComments=true,Audio="AAC128",Passes=twoPass?2:1,RateControl=twoPass?"Bitrate":"Quality",BitrateMbps=2};
                using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(2));
                var r=await EncoderJob.Run(input,s,new SilentProgress(),timeout.Token,3);
                var metadata=Mp4RecordingMetadata.Read(r.Output,timeout.Token);
                if(metadata!=r.Metadata)throw new Exception("MP4 metadata round-trip failed");
                var comments=Mp4CommentData.TryRead(r.Output,timeout.Token)??throw new Exception("Embedded comments missing");
                var config=Config.Load(false);config.NicoJkLogFolder=Path.Combine(EncoderPaths.Root,"temp", "test-results","no-log-folder");
                var withoutLogs=LogReader.Prepare(r.Output,config,timeout.Token);
                if(comments.Comments.Count==0 || !comments.ForPlayback().SequenceEqual(withoutLogs.Comments) || withoutLogs.Files!=0)throw new Exception("Embedded-only playback failed");
                results.Add(new{codec=s.Codec,backend=s.Backend,passes=s.Passes,width=s.Width,height=s.Height,fps=s.FrameRate,audio=s.Audio,embeddedComments=comments.Comments.Count,withoutExternalLogs=true,output=r.Output,metadata});
            }
            string serialized=JsonSerializer.Serialize(new EncoderSettings {Width=1600,Height=900,Codec="H265",Quality=27,Passes=twoPass?2:1},EncoderSettings.Json);
            var restored=JsonSerializer.Deserialize<EncoderSettings>(serialized)!;
            if(restored.Width!=1600 || restored.Height!=900 || restored.Codec!="H265" || restored.Quality!=27 || restored.Passes!=(twoPass?2:1))throw new Exception("Settings persistence round-trip failed");
            File.WriteAllText(Path.Combine(EncoderPaths.Root,"temp", "test-results",twoPass?"encoder-passes-test.json":"encoder-smoke.json"),JsonSerializer.Serialize(new{passed=true,results,settingsPersistence=true},EncoderSettings.Json));return 0;
        }
        catch(Exception ex)
        {
            File.WriteAllText(Path.Combine(EncoderPaths.Root,"temp", "test-results",twoPass?"encoder-passes-test.json":"encoder-smoke.json"),JsonSerializer.Serialize(new{passed=false,results,error=ex.ToString()},EncoderSettings.Json));return 1;
        }
    }
}
