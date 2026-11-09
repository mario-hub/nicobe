using System.IO;
using System.Text;
using System.Text.Json;

namespace NicoBE.Encoder;

// Short generated tones only; no user recordings or saved settings are modified.
internal static class AudioTest
{
    public static async Task<int> Run()
    {
        var checks = new List<string>();
        string folder = Path.Combine(EncoderPaths.Root, "temp", "test-results", "audio-test");
        Directory.CreateDirectory(folder);
        void Check(bool condition, string name) { if (!condition) throw new Exception(name); checks.Add(name); }
        try
        {
            var s = EncoderSettings.Load();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var token = timeout.Token;
            Task<string> F(params string[] args) => EncoderJob.Capture(s.Ffmpeg, new[] { "-v", "error", "-nostdin", "-y" }.Concat(args), token);
            string FileAt(string name) => Path.Combine(folder, name);
            await F("-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=1.5", "-c:a", "aac", "-b:a", "128k", "-flags:a", "+bitexact", FileAt("main.aac"));
            await F("-f", "lavfi", "-i", "sine=frequency=880:sample_rate=48000:duration=1.5", "-c:a", "aac", "-b:a", "128k", "-flags:a", "+bitexact", FileAt("sub.aac"));
            await F("-f", "lavfi", "-i", "aevalsrc=0.1*sin(2*PI*440*t)|0.1*sin(2*PI*880*t):s=48000:d=0.8", "-c:a", "aac", "-b:a", "128k", "-flags:a", "+bitexact", FileAt("stereo.aac"));
            var main = Frames(File.ReadAllBytes(FileAt("main.aac")));
            var sub = Frames(File.ReadAllBytes(FileAt("sub.aac")));
            using (var output = File.Create(FileAt("dual.aac")))
            {
                for (int i = 0; i < Math.Min(main.Count, sub.Count); i++) output.Write(DualFrame(main[i], sub[i]));
                output.Write(File.ReadAllBytes(FileAt("stereo.aac")));
            }
            await F("-f", "lavfi", "-i", "color=s=320x180:r=30:d=2.4", "-itsoffset", "0.15", "-i", FileAt("dual.aac"), "-map", "0:v", "-map", "1:a", "-c:v", "libx264", "-preset", "ultrafast", "-c:a", "copy", "-t", "2.4", "-mpegts_service_id", "1", FileAt("base.ts"));
            var beginning = new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.FromHours(9));
            var recording = new RecordingInfo(FileAt("dual.ts"), 1, 1, 256, beginning, beginning.AddMinutes(1), 0);
            byte[] pat = Section(new byte[] {0,0xb0,0,0,1,0xc1,0,0,0,1,0xf0,0});
            byte[] pmt = Section(new byte[] {2,0xb0,0,0,1,0xc1,0,0,0xe1,0,0xf0,0,0x1b,0xe1,0,0xf0,0,0x0f,0xe1,1,0xf0,3,0x52,1,0x10});
            byte[] Eit(byte component, bool reverse = false)
            {
                int mjd = (int)(beginning.Date - new DateTime(1858,11,17)).TotalDays;
                var data = new List<byte> {0x4e,0xb0,0,0,1,0xc1,0,0,0,1,0,1,0,0x4e,0,1,(byte)(mjd>>8),(byte)mjd,0x12,0,0,0,1,0,0x80,14,0xc4,12,0xf2,component,0x10,0x0f,0xff,0xcf};
                data.AddRange(Encoding.ASCII.GetBytes(reverse ? "engjpn" : "jpneng"));
                return Section(data.ToArray());
            }
            using (var output = File.Create(recording.Path))
            {
                output.Write(Packet(0, pat)); output.Write(Packet(4096, pmt)); output.Write(Packet(18, Eit(2)));
                output.Write(File.ReadAllBytes(FileAt("base.ts")));
            }
            using var probe = await EncoderJob.Probe(s.Ffprobe, recording.Path, token);
            var streams = probe.RootElement.GetProperty("streams").EnumerateArray().ToArray();
            var audio = streams.Where(x=>x.GetProperty("codec_type").GetString()=="audio").Select(x=>x.GetProperty("index").GetInt32()).ToArray();
            int video = streams.First(x=>x.GetProperty("codec_type").GetString()=="video").GetProperty("index").GetInt32();
            var plan = DualMonoAudio.Detect(recording.Path, recording, streams, token);
            Check(plan.Count == 1 && plan[audio[0]], "Detect Japanese/English dual mono from TS descriptors");
            var ordinary = new DualMonoAudio.Tables(recording); ordinary.Consume(0, pat); ordinary.Consume(4096,pmt); ordinary.Consume(18,Eit(3));
            Check(ordinary.Result.Count == 0, "Do not split ordinary stereo");
            var reversed = new DualMonoAudio.Tables(recording); reversed.Consume(0,pat); reversed.Consume(4096,pmt); reversed.Consume(18,Eit(2,true));
            Check(reversed.Result.Count == 1 && !reversed.Result[257], "Handle Japanese on the secondary channel");
            var unrelated = new DualMonoAudio.Tables(recording with {Start=beginning.AddHours(1),End=beginning.AddHours(2)});
            unrelated.Consume(0,pat); unrelated.Consume(4096,pmt); unrelated.Consume(18,Eit(2));
            Check(unrelated.Result.Count == 0, "Ignore unrelated program events");
            s.Codec="H264";s.Backend="CPU";s.Speed="Fast";s.Width=320;s.Height=180;s.FrameRate=30;s.Passes=1;s.RateControl="Quality";s.Deinterlace="Off";
            foreach(string container in new[]{"MP4","MKV"})
            {
                s.Container=container;s.Audio=container=="MKV"?"Copy":"AAC128";
                string output=FileAt("result."+container.ToLowerInvariant());
                if(File.Exists(output))File.Delete(output);
                await EncoderJob.Capture(s.Ffmpeg,EncoderJob.Arguments(s,recording.Path,output,video,audio,2.4,dualMono:plan),token);
                using var result=await EncoderJob.Probe(s.Ffprobe,output,token);
                EncoderJob.ValidateAudio(result.RootElement,audio,plan);
                var outputAudio=result.RootElement.GetProperty("streams").EnumerateArray().Where(x=>x.GetProperty("codec_type").GetString()=="audio").ToArray();
                double Start(JsonElement stream)=>double.Parse(stream.GetProperty("start_time").GetString()!,System.Globalization.CultureInfo.InvariantCulture);
                double sourceDelta=Start(streams.First(x=>x.GetProperty("index").GetInt32()==audio[0]))-Start(streams.First(x=>x.GetProperty("index").GetInt32()==video));
                double targetDelta=Start(outputAudio[0])-Start(result.RootElement.GetProperty("streams").EnumerateArray().First(x=>x.GetProperty("codec_type").GetString()=="video"));
                Check(Math.Abs(sourceDelta-targetDelta)<.025,container+" preserves audio/video offset within AAC encoder delay");
                Check(Math.Abs(double.Parse(outputAudio[0].GetProperty("start_time").GetString()!,System.Globalization.CultureInfo.InvariantCulture)-double.Parse(outputAudio[1].GetProperty("start_time").GetString()!,System.Globalization.CultureInfo.InvariantCulture))<.002,container+" audio timestamps align");
                for(int track=0;track<2;track++)
                {
                    string pcm=FileAt("check.pcm");
                    await F("-ss","0.4","-i",output,"-map","0:a:"+track,"-t","0.2","-ar","48000","-ac","2","-c:a","pcm_s16le","-f","s16le",pcm);
                    var bytes=File.ReadAllBytes(pcm);
                    Check(Math.Abs(Frequency(bytes,0)-(track==0?440:880))<20 && Math.Abs(Frequency(bytes,1)-(track==0?440:880))<20,container+" separated track "+track);
                    await F("-ss","1.9","-i",output,"-map","0:a:"+track,"-t","0.2","-ar","48000","-ac","2","-c:a","pcm_s16le","-f","s16le",pcm);
                    bytes=File.ReadAllBytes(pcm);
                    Check(Math.Abs(Frequency(bytes,0)-440)<20 && Math.Abs(Frequency(bytes,1)-880)<20,container+" preserves a stereo segment in track "+track);
                }
                checks.Add(container+" languages and Japanese default"+(s.Audio=="Copy"?" (copy override)":""));
            }
            File.WriteAllText(Path.Combine(folder,"checks.json"),JsonSerializer.Serialize(new{passed=true,checks},EncoderSettings.Json));return 0;
        }
        catch(Exception error){File.WriteAllText(Path.Combine(folder,"checks.json"),JsonSerializer.Serialize(new{passed=false,checks,error=error.ToString()},EncoderSettings.Json));return 1;}
    }
    static double Frequency(byte[] data,int channel)
    {
        int crossings=0,frames=data.Length/4;
        for(int i=1;i<frames;i++)if(BitConverter.ToInt16(data,(i-1)*4+channel*2)<=0 && BitConverter.ToInt16(data,i*4+channel*2)>0)crossings++;
        return crossings*48000d/Math.Max(1,frames-1);
    }
    static List<byte[]> Frames(byte[] data)
    {
        var frames=new List<byte[]>();
        for(int i=0;i+7<=data.Length;)
        {
            int length=((data[i+3]&3)<<11)|(data[i+4]<<3)|(data[i+5]>>5);
            if(length<7||i+length>data.Length)throw new Exception("ADTS fixture");
            frames.Add(data[i..(i+length)]);i+=length;
        }
        return frames;
    }
    static byte[] DualFrame(byte[] first,byte[] second)
    {
        var bits=new List<int>();
        void Put(int value,int count){for(int bit=count-1;bit>=0;bit--)bits.Add((value>>bit)&1);}
        Put(5,3);Put(0,4);Put(1,2);Put(3,4);Put(2,4);Put(0,4);Put(0,4);Put(0,2);Put(0,3);Put(0,4);Put(0,3);Put(0,1);Put(0,4);Put(0,1);Put(1,4);
        while(bits.Count%8!=0)Put(0,1);Put(0,8); // PCE: two front SCEs, no comment.
        void Mono(byte[] frame,int tag)
        {
            var body=frame.Skip(7).SelectMany(b=>Enumerable.Range(0,8).Select(i=>(b>>(7-i))&1)).ToList();
            if(body.Take(3).Any(x=>x!=0))throw new Exception("Expected a single-channel AAC element");
            int end=body.FindLastIndex(x=>x==1)-2;
            if(end<7||body.Skip(end).Take(3).Any(x=>x!=1))throw new Exception("AAC end marker");
            Put(0,3);Put(tag,4);bits.AddRange(body.Skip(7).Take(end-7));
        }
        Mono(first,0);Mono(second,1);Put(7,3);while(bits.Count%8!=0)Put(0,1);
        int length=7+bits.Count/8;var output=new byte[length];Array.Copy(first,output,7);
        output[2]&=0xfe;output[3]=(byte)((output[3]&0x3c)|(length>>11));output[4]=(byte)(length>>3);output[5]=(byte)(((length&7)<<5)|31);output[6]=0xfc;
        for(int i=0;i<bits.Count;i++)output[7+i/8]|=(byte)(bits[i]<<(7-i%8));return output;
    }
    static byte[] Section(byte[] data)
    {
        int length=data.Length+1;data[1]=(byte)((data[1]&0xf0)|(length>>8));data[2]=(byte)length;
        uint crc=DualMonoAudio.Crc(data);return data.Concat(new[]{(byte)(crc>>24),(byte)(crc>>16),(byte)(crc>>8),(byte)crc}).ToArray();
    }
    static byte[] Packet(int pid,byte[] section)
    {
        var packet=Enumerable.Repeat((byte)0xff,188).ToArray();packet[0]=0x47;packet[1]=(byte)(0x40|(pid>>8));packet[2]=(byte)pid;packet[3]=0x10;packet[4]=0;section.CopyTo(packet,5);return packet;
    }
}
