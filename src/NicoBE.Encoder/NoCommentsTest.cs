using System.IO;
using System.Text.Json;

namespace NicoBE.Encoder;

internal static class NoCommentsTest
{
    internal static async Task<int> Run(string input)
    {
        var results=new List<object>();
        string root=Path.Combine(EncoderPaths.Root,"temp", "test-results","no-comments");Directory.CreateDirectory(root);
        try
        {
            foreach(string container in new[]{"MP4","MKV"})
            {
                string logs=Path.Combine(root,"logs-"+container);Directory.CreateDirectory(logs);
                if(container=="MP4")
                    foreach(string channel in ChannelMap.Default.Values.Concat(Config.Load(false).ChannelOverrides.Values).Distinct())Directory.CreateDirectory(Path.Combine(logs,channel));
                var settings=EncoderSettings.Load();settings.Container=container;settings.Backend="CPU";settings.Codec="H264";
                settings.Width=1280;settings.Height=720;settings.FrameRate=30;settings.Passes=1;settings.RateControl="Quality";settings.Quality=23;settings.Speed="Fast";
                settings.EmbedComments=true;settings.SaveExternalAss=true;settings.NicoJkLogFolder=logs;settings.OutputFolder=root;
                using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(1));
                var result=await EncoderJob.Run(input,settings,new Progress<EncodeProgress>(),timeout.Token,3);
                if(!File.Exists(result.Output) || result.Warnings.Count!=1 || File.Exists(Path.ChangeExtension(result.Output,".ass")))throw new Exception("Missing-video/comment warning result");
                if(!File.ReadAllText(result.Log).Contains(result.Warnings[0]))throw new Exception("Warning log missing");
                if(container=="MP4")
                {
                    if(Mp4CommentData.TryRead(result.Output,timeout.Token)!=null || Mp4RecordingMetadata.Read(result.Output,timeout.Token)!=result.Metadata)throw new Exception("MP4 output metadata/comments");
                    if(!result.Warnings[0].Contains("0件"))throw new Exception("Empty log warning");
                }
                else
                {
                    using var probe=await EncoderJob.Probe(settings.Ffprobe,result.Output,timeout.Token);
                    if(probe.RootElement.GetProperty("streams").EnumerateArray().Any(s=>s.GetProperty("codec_type").GetString()=="subtitle"))throw new Exception("Unexpected empty ASS track");
                    if(ContainerComments.Metadata(probe.RootElement.GetProperty("format"))!=result.Metadata)throw new Exception("MKV metadata missing");
                }
                results.Add(new{container,result.Output,result.Warnings});
            }
            File.WriteAllText(Path.Combine(root,"result.json"),JsonSerializer.Serialize(new{passed=true,results},EncoderSettings.Json));return 0;
        }
        catch(Exception ex){File.WriteAllText(Path.Combine(root,"result.json"),JsonSerializer.Serialize(new{passed=false,results,error=ex.ToString()},EncoderSettings.Json));return 1;}
    }
}
