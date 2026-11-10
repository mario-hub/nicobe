using System.IO;
using System.Text.Json;
using NicoBE.Encoder;

namespace NicoBE.Encoder.Cli;

internal static class CliTest
{
    public static int Run()
    {
        try
        {
            string root=Path.Combine(EncoderPaths.Root,"temp", "test-results","cli-"+Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(root);
            string profile=Path.Combine(root,"設定.json"),input=Path.Combine(root,"入力.ts");
            File.WriteAllText(input,"fixture; not a recording");
            Directory.CreateDirectory(Path.Combine(root,"tools"));
            File.WriteAllText(Path.Combine(root,"tools","ffmpeg.exe"),"fixture; not an executable");
            File.WriteAllText(Path.Combine(root,"tools","ffprobe.exe"),"fixture; not an executable");
            var settings=new EncoderSettings {Ffmpeg="tools/ffmpeg.exe",OutputFolder="output",Backend="CPU",EmbedComments=false,NicoJkLogFolder="logs"};
            string original=JsonSerializer.Serialize(settings,EncoderSettings.Json);File.WriteAllText(profile,original);
            void Check(bool value,string message){if(!value)throw new Exception(message);}
            void Reject(Action action,string message){bool failed=false;try{action();}catch{failed=true;}Check(failed,message);}
            var parsed=CliRequest.Parse(new[]{input,"--settings",profile});
            var prepared=parsed.Prepare();
            Check(prepared.Input==input && prepared.OutputFile==null,"input / omitted output");
            Check(prepared.Settings.OutputFolder==Path.Combine(root,"output"),"profile-relative output");
            Check(prepared.Settings.Ffmpeg==Path.Combine(root,"tools","ffmpeg.exe"),"profile-relative ffmpeg");
            Check(!Directory.Exists(prepared.Settings.OutputFolder),"validation must not create output");
            string named=Path.Combine(root,"result.mkv");
            var explicitFile=CliRequest.Parse(new[]{"--settings",profile,input,named}).Prepare();
            Check(explicitFile.Settings.Container=="MKV" && explicitFile.OutputFile==named,"extension overrides profile container");
            Check(EncoderJob.ResolveOutputPath(input,explicitFile.Settings,named)==named,"explicit output is exact");
            string namedDirectory=Path.Combine(root,"folder.mp4");Directory.CreateDirectory(namedDirectory);
            var directory=CliRequest.Parse(new[]{input,namedDirectory,"--settings",profile}).Prepare();
            Check(directory.OutputFile==null && directory.Settings.OutputFolder==namedDirectory,"existing directory with movie suffix");
            string generated=EncoderJob.ResolveOutputPath(input,prepared.Settings);
            Check(Path.GetFileName(generated)=="入力.mp4","automatic output only changes extension");
            Check(Path.GetFileName(EncoderJob.ResolveOutputPath(input,explicitFile.Settings))=="入力.mkv","MKV keeps source name");
            string longName=new string('あ',160)+".ts";
            Check(Path.GetFileName(EncoderJob.ResolveOutputPath(Path.Combine(root,longName),prepared.Settings))==Path.ChangeExtension(longName,".mp4"),"long source names are not truncated");
            Directory.CreateDirectory(prepared.Settings.OutputFolder);File.WriteAllText(generated,"existing video");
            Check(Path.GetFileName(EncoderJob.ResolveOutputPath(input,prepared.Settings))=="入力 (2).mp4","only collisions add a sequence number");
            File.WriteAllText(Path.Combine(prepared.Settings.OutputFolder,"入力 (2).ass"),"existing ASS");
            Check(Path.GetFileName(EncoderJob.ResolveOutputPath(input,prepared.Settings))=="入力 (3).mp4","sidecar collision uses the same sequence number");
            File.WriteAllText(named,"existing video");
            Reject(()=>CliRequest.Parse(new[]{input,named,"--settings",profile}).Prepare(),"must refuse existing explicit video");
            string assOutput=Path.Combine(root,"with-ass.mp4");File.WriteAllText(Path.ChangeExtension(assOutput,".ass"),"existing ASS");
            Reject(()=>CliRequest.Parse(new[]{input,assOutput,"--settings",profile}).Prepare(),"must refuse existing ASS");
            Reject(()=>CliRequest.Parse(Array.Empty<string>()),"missing input");
            Reject(()=>CliRequest.Parse(new[]{input,"out","extra"}),"extra positional argument");
            Reject(()=>CliRequest.Parse(new[]{input,"--unknown"}),"unknown option");
            Reject(()=>CliRequest.Parse(new[]{input,"--settings"}),"missing profile argument");
            Reject(()=>CliRequest.Parse(new[]{input,"--settings",profile,"--settings",profile}),"duplicate profile");
            Reject(()=>new CliRequest(input,null,Path.Combine(root,"missing.json")).Prepare(),"explicit missing profile");
            string invalid=Path.Combine(root,"invalid.json");File.WriteAllText(invalid,"null");
            Reject(()=>new CliRequest(input,null,invalid).Prepare(),"null profile");
            File.WriteAllText(invalid,"{\"Codecc\":\"H264\"}");
            Reject(()=>new CliRequest(input,null,invalid).Prepare(),"unknown profile property");
            Check(CliRequest.Parse(new[]{"--","-input.ts"}).Input=="-input.ts","option terminator");
            Check(File.ReadAllText(profile)==original && File.ReadAllText(named)=="existing video","profile and existing output preserved");
            Console.WriteLine("PASS: CLI parsing, profile paths, output naming/format, collisions, invalid profiles, no settings writes. No network or encoding used.");
            return 0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
