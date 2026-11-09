using System.Collections.Concurrent;
using System.IO;

namespace NicoBE.Encoder;

internal static class EncoderCapabilities
{
    static readonly ConcurrentDictionary<string,Task<bool>> cache=new();
    public static bool NeedsProbe(string codec,string backend)=>backend=="NVENC" || codec=="AV1" && backend is "QSV" or "AMF";
    public static Task<bool> GpuAvailable(string ffmpeg,string codec,string backend)
    {
        if(!File.Exists(ffmpeg))return Task.FromResult(false);
        string key=Path.GetFullPath(ffmpeg)+"|"+File.GetLastWriteTimeUtc(ffmpeg).Ticks+"|"+codec+"|"+backend;
        return cache.GetOrAdd(key,_=>Probe(ffmpeg,codec,backend));
    }
    static async Task<bool> Probe(string ffmpeg,string codec,string backend)
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            // Actually open the encoder: listing encoders alone does not test the GPU/driver.
            await EncoderJob.Capture(ffmpeg,new[]{"-v","error","-nostdin","-f","lavfi","-i","color=s=256x144:r=30",
                "-frames:v","1","-pix_fmt","yuv420p","-c:v",EncoderJob.EncoderName(new() {Codec=codec,Backend=backend}),"-f","null","-"},timeout.Token);
            return true;
        }
        catch{return false;}
    }
    public static async Task<bool> Resolve(EncoderSettings settings,CancellationToken token=default)
    {
        if(!NeedsProbe(settings.Codec,settings.Backend))return false;
        bool available=await GpuAvailable(settings.Ffmpeg,settings.Codec,settings.Backend).WaitAsync(token);
        if(available)return false;
        settings.Backend="CPU";
        settings.Quality=Math.Clamp(settings.Quality,EncoderSettings.QualityMinimum(settings.Codec,settings.Backend),EncoderSettings.QualityMaximum(settings.Codec,settings.Backend));
        if(settings.RateControl!="Bitrate")settings.Passes=1;
        return true;
    }
}
