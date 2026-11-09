using System.IO;
using System.Text.Json;

namespace NicoBE.Encoder;

internal static class Av1Test
{
    internal static async Task<int> Run(string input)
    {
        var results=new List<object>();var availability=new Dictionary<string,bool>();
        string report=Path.Combine(EncoderPaths.Root,"temp", "test-results","av1-test.json");
        Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        try
        {
            var configured=EncoderSettings.Load();
            foreach(string backend in new[]{"NVENC","QSV","AMF"})
            {
                availability[backend]=await EncoderCapabilities.GpuAvailable(configured.Ffmpeg,"AV1",backend);
                var check=EncoderSettings.Load();check.Codec="AV1";check.Backend=backend;
                check.RateControl="Quality";check.Passes=backend=="NVENC"?2:1;
                bool fallback=await EncoderCapabilities.Resolve(check);
                if(fallback==availability[backend] || fallback && (check.Backend!="CPU" || check.Passes!=1))throw new Exception("AV1 fallback failed: "+backend);
            }
            var cases=new List<(string Backend,string Container,string Rate,int Passes)> {("CPU","MP4","Quality",1),("CPU","MKV","Bitrate",2)};
            cases.AddRange(availability.Where(x=>x.Value).Select(x=>(x.Key,"MP4","Quality",x.Key=="NVENC"?2:1)));
            foreach(var test in cases)
            {
                var s=EncoderSettings.Load();s.Codec="AV1";s.Backend=test.Backend;s.Container=test.Container;s.RateControl=test.Rate;s.Passes=test.Passes;
                s.Speed="Fast";s.Width=1280;s.Height=720;s.FrameRate=30;s.Quality=35;s.BitrateMbps=1.5;
                s.EmbedComments=true;s.SaveExternalAss=true;s.CommentSizePercent=150;
                s.OutputFolder=Path.Combine(EncoderPaths.Root,"temp", "test-results","av1-output");
                using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(2));
                var output=await EncoderJob.Run(input,s,new Progress<EncodeProgress>(),timeout.Token,3);
                var config=Config.Load(false);config.NicoJkLogFolder=Path.Combine(EncoderPaths.Temp,"missing-logs");config.CommentPreference="Original";
                var comments=LogReader.Prepare(output.Output,config,timeout.Token);
                string ass=Path.ChangeExtension(output.Output,".ass");
                if(comments.Comments.Count==0 || AssComments.ReadFile(ass,timeout.Token).Count==0)throw new Exception("AV1 comment playback");
                if(s.Container=="MP4" && Mp4CommentData.TryRead(output.Output,timeout.Token)==null)throw new Exception("AV1 original comments missing");
                if(s.Container=="MKV")
                {
                    string embedded=await EncoderJob.Capture(s.Ffmpeg,new[]{"-v","error","-nostdin","-i",output.Output,"-map","0:s:0","-f","ass","pipe:1"},timeout.Token);
                    if(AssComments.Read(embedded,timeout.Token).Count==0)throw new Exception("AV1 MKV embedded ASS missing");
                }
                results.Add(new{s.Codec,s.Backend,s.Container,s.RateControl,s.Passes,comments=comments.Comments.Count,output=output.Output});
            }
            File.WriteAllText(report,JsonSerializer.Serialize(new{passed=true,availability,results},EncoderSettings.Json));return 0;
        }
        catch(Exception ex){File.WriteAllText(report,JsonSerializer.Serialize(new{passed=false,availability,results,error=ex.ToString()},EncoderSettings.Json));return 1;}
    }
}
