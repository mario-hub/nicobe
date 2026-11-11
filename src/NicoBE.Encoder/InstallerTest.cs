using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NicoBE.Encoder;

internal static class InstallerTest
{
    sealed class Feed(byte[] zip,bool badHash=false) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            HttpContent content=request.RequestUri!.AbsolutePath.EndsWith(".sha256")
                ?new StringContent(badHash?new string('0',64):Convert.ToHexString(SHA256.HashData(zip)))
                :new ByteArrayContent(zip);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {Content=content});
        }
    }
    sealed class SilentProgress : IProgress<InstallProgress> {public void Report(InstallProgress value){}}
    static byte[] Zip(params string[] names)
    {
        using var memory=new MemoryStream();
        using(var zip=new ZipArchive(memory,ZipArchiveMode.Create,true))
            foreach(string name in names){using var entry=zip.CreateEntry(name).Open();entry.Write(Encoding.UTF8.GetBytes("fixture; not an executable"));}
        return memory.ToArray();
    }
    internal static async Task Run()
    {
        string root=Path.Combine(EncoderPaths.Root,"temp", "test-results","installer-"+Guid.NewGuid().ToString("N")[..8]);
        var cases=new List<string>();const string version="ffmpeg-9.0.2-essentials_build";
        var zip=Zip(version+"/bin/ffmpeg.exe",version+"/bin/ffprobe.exe",version+"/LICENSE");
        using(var client=new HttpClient(new Feed(zip)))
        {
            string installed=await FfmpegInstaller.Install(client,root,new SilentProgress(),CancellationToken.None);
            if(!File.Exists(installed)||!File.Exists(Path.Combine(Path.GetDirectoryName(installed)!,"ffprobe.exe")))throw new Exception("Installer success test failed.");
            if(installed!=Path.Combine(root,version,"bin","ffmpeg.exe")||!File.Exists(Path.Combine(root,version,"LICENSE")))throw new Exception("Versioned layout or license not preserved.");
            cases.Add("versioned directory preserved without installation ID layer");
            string second=await FfmpegInstaller.Install(client,root,new SilentProgress(),CancellationToken.None);
            if(second!=Path.Combine(root,version+"-0002","bin","ffmpeg.exe")||!File.Exists(installed))throw new Exception("Repeated installation overwrote an existing build.");
            cases.Add("reinstall uses a short suffix and preserves the previous build");
        }
        async Task Reject(byte[] data,string name,bool badHash=false,CancellationToken token=default)
        {
            using var client=new HttpClient(new Feed(data,badHash));bool rejected=false;
            try{await FfmpegInstaller.Install(client,root,new SilentProgress(),token);}
            catch(Exception ex) when(ex is IOException or OperationCanceledException){rejected=true;}
            if(!rejected||Directory.GetDirectories(root,".incoming-*").Length!=0||Directory.GetDirectories(root).Length!=2)throw new Exception("Installer rejection test failed: "+name);
            cases.Add(name);
        }
        await Reject(zip,"checksum mismatch preserves existing installation",true);
        await Reject(Zip("../../../escape.txt","build/bin/ffmpeg.exe","build/bin/ffprobe.exe"),"unsafe ZIP paths rejected before extraction");
        await Reject(Zip("build/bin/ffmpeg.exe"),"missing ffprobe rejected");
        await Reject(zip,"cancellation removes staging files",token:new CancellationToken(true));
        File.WriteAllText(Path.Combine(EncoderPaths.Root,"temp", "test-results","installer-test.json"),JsonSerializer.Serialize(new{passed=true,networkUsed=false,cases},EncoderSettings.Json));
    }
}
