namespace NicoBE;

internal static class ChannelMapTest
{
    internal static void Verify(Action<bool,string> check)
    {
        // NicoJK jkch.sh.txt, checked 2026-11-17. Explicit exceptions cover
        // subchannels; other listed BS services use the same-number jk channel.
        int[] services={101,102,103,104,141,142,143,151,152,153,161,162,163,171,172,173,181,182,183,
            191,192,193,200,201,202,211,222,231,232,234,236,238,241,242,243,244,245,251,252,255,256,258,260,263,265,910};
        foreach(int service in services)
        {
            int channel=service switch{102=>101,104=>103,142 or 143=>141,152 or 153=>151,162 or 163=>161,
                172 or 173=>171,182 or 183=>181,232=>231,_=>service};
            check(ChannelMap.Default.GetValueOrDefault($"4:{service}")==$"jk{channel}",$"BS service {service} maps to jk{channel}");
        }
        check(!ChannelMap.Default.ContainsKey("4:531")&&!ChannelMap.Default.ContainsKey("4:700")
            &&!ChannelMap.Default.ContainsKey("4:701")&&!ChannelMap.Default.ContainsKey("4:791"),"Undefined BS services are not assigned invented channels");
        check(ChannelMap.Default["15:1040"]=="jk4","Terrestrial mapping preserved");
        if(ReleaseTestEnvironment.Root!=null)
        {
            var now=DateTimeOffset.UtcNow;
            var info=new RecordingInfo("synthetic.ts",101,4,0,now,now.AddMinutes(1),0);
            check(LogReader.ResolveChannel(info,new Config())=="jk101","Actual resolver identifies NHK BS");
            check(LogReader.ResolveChannel(info,new Config{ChannelOverrides=new(){["4:101"]="jk103"}})=="jk103","User override retains priority");
            string file=System.IO.Path.Combine(Paths.Root,"channels.json");
            if(System.IO.File.Exists(file))throw new Exception("Requires fresh test directory without channels.json");
            try
            {
                System.IO.File.WriteAllText(file,"{\"4:102\":\"jk103\"}");
                check(LogReader.ResolveChannel(info,new Config())=="jk101","Partial local channel map falls back to built-in BS mapping");
                check(LogReader.ResolveChannel(info with {ServiceId=102},new Config())=="jk103","Explicit local channel mapping retains priority");
            }
            finally{System.IO.File.Delete(file);}
        }
    }
}
