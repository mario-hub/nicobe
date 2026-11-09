using System.IO;
using System.Text;
using System.Text.Json;

namespace NicoBE;

internal static class LiveCommentTest
{
    static void Check(bool value,string name){if(!value)throw new Exception(name);}
    static byte[] Section(int pid,byte[] section)
    {
        var p=Enumerable.Repeat((byte)255,188).ToArray();p[0]=0x47;p[1]=(byte)(0x40|(pid>>8));p[2]=(byte)pid;p[3]=0x10;p[4]=0;
        section.CopyTo(p,5);return p;
    }
    static byte[] Pcr(int seconds)
    {
        long n=seconds*90000L;var p=new byte[188];p[0]=0x47;p[1]=1;p[2]=1;p[3]=0x20;p[4]=183;p[5]=0x10;
        p[6]=(byte)(n>>25);p[7]=(byte)(n>>17);p[8]=(byte)(n>>9);p[9]=(byte)(n>>1);p[10]=(byte)((n&1)<<7);return p;
    }
    static void Recording(string path,DateTimeOffset time)
    {
        var jst=time.ToOffset(TimeSpan.FromHours(9));int mjd=(int)(jst.Date-new DateTime(1858,11,17)).TotalDays;
        byte Bcd(int n)=>(byte)((n/10<<4)|n%10);
        using var output=File.Create(path);
        output.Write(Section(0,new byte[]{0,0xB0,13,0,1,0xC1,0,0,0,1,0xE1,0,0,0,0,0}));
        output.Write(Section(256,new byte[]{2,0xB0,18,0,1,0xC1,0,0,0xE1,1,0xF0,0,0x1B,0xE1,1,0xF0,0,0,0,0,0}));
        output.Write(Section(17,new byte[]{0x42,0xB0,12,0,1,0xC1,0,0,0,15,0,0,0,0,0}));
        output.Write(Pcr(0));
        output.Write(Section(20,new byte[]{0x70,0x70,5,(byte)(mjd>>8),(byte)mjd,Bcd(jst.Hour),Bcd(jst.Minute),Bcd(jst.Second)}));
        for(int i=1;i<=10;i++)output.Write(Pcr(i));
    }
    static void Append(string path,ReadOnlySpan<byte> bytes)
    {using var stream=new FileStream(path,FileMode.Append,FileAccess.Write,FileShare.ReadWrite);stream.Write(bytes);}
    public static int Run()
    {
        try
        {
            string root=Path.Combine(Paths.Root,"temp", "test-results","live-"+Guid.NewGuid().ToString("N")[..8]);Directory.CreateDirectory(root);
            string ts=Path.Combine(root,"growing.ts"),logs=Path.Combine(root,"logs"),channel=Path.Combine(logs,"jk4");Directory.CreateDirectory(channel);
            var epoch=DateTimeOffset.FromUnixTimeSeconds(1700000000);long stamp=epoch.ToUnixTimeSeconds();Recording(ts,epoch);
            var config=new Config {NicoJkLogFolder=logs,ChannelOverrides=new() {["15:1"]="jk4"}};
            string Chat(int second,string text)=>$"<chat thread=\"a\" no=\"{second}\" date=\"{stamp+second}\" user_id=\"user\">{text}</chat>\n";
            string first=Path.Combine(channel,(stamp-1)+".txt");File.WriteAllText(first,Chat(1,"最初"),new UTF8Encoding(false));
            var info=TsReader.Read(ts,null,CancellationToken.None);
            Check(info.Start==epoch,"fixture clock");
            var source=new LiveCommentSource(info,config);
            var batch=source.Read(true,CancellationToken.None);Check(batch.Added.Count==1,"initial read");
            long read=source.BytesRead;
            Check(source.Read(false,CancellationToken.None).Added.Count==0 && source.BytesRead==read,"unchanged file not reread");
            byte[] split=Encoding.UTF8.GetBytes(Chat(2,"改\n行"));int cut=Encoding.UTF8.GetString(split).IndexOf('改');
            // ASCII prefix followed by just one byte of a three-byte UTF-8 character.
            Append(first,split.AsSpan(0,cut+1));batch=source.Read(false,CancellationToken.None);
            Check(batch.Added.Count==0 && batch.Malformed==0,"partial UTF-8/chat waits");
            Append(first,split.AsSpan(cut+1));batch=source.Read(false,CancellationToken.None);
            Check(batch.Added.Single().Text=="改\n行","multiline UTF-8 continuation");
            Check(source.BytesRead==read+split.Length,"only appended bytes read");
            string second=Path.Combine(channel,(stamp+3)+".txt");File.WriteAllText(second,Chat(1,"最初")+Chat(3,"切替"));
            batch=source.Read(true,CancellationToken.None);Check(batch.Added.Count==1 && batch.Added[0].Text=="切替","rotation and deduplication");
            Append(second,Encoding.UTF8.GetBytes(Chat(40,"未来")+Chat(4,"現在")));
            batch=source.Read(false,CancellationToken.None);Check(batch.Added.Single().Text=="現在","current TS boundary");
            read=source.BytesRead;Append(ts,Pcr(60));batch=source.Read(false,CancellationToken.None);
            Check(batch.Added.Single().Text=="未来" && source.BytesRead==read,"growth releases deferred comments without rereading");
            File.WriteAllText(first,Chat(5,"再作成"));batch=source.Read(false,CancellationToken.None);
            Check(batch.Added.Any(c=>c.Text=="再作成"),"truncated log recovers");
            Append(first,Encoding.UTF8.GetBytes(Chat(6,"改行なし").TrimEnd('\n')));batch=source.Read(false,CancellationToken.None);
            Check(batch.Added.Single().Text=="改行なし","complete chat without final newline");

            var engine=new CommentEngine(new());engine.Measure=_=>(20,30,null);
            Comment C(double t,string text)=>new(t,text,"#FFFFFF",1,CommentPosition.Scroll);
            engine.SetComments(new[]{C(1,"old"),C(4,"next")});engine.SetPlaying(true,0);engine.OnPosition(1,0);
            var visible=engine.Active.Single();engine.AppendComments(new[]{C(5,"append")});
            Check(ReferenceEquals(engine.Active.Single(),visible),"append preserves visible comments");
            engine.AppendComments(new[]{C(.5,"late"),C(3,"middle")});engine.OnPosition(3,1);
            Check(engine.Active.Count(a=>a.Comment.Text=="old")==1 && engine.Active.Count(a=>a.Comment.Text=="middle")==1,"late merge keeps cursor without duplicates");
            engine.SetPlaying(false,1);double x=engine.X(visible);engine.AppendComments(new[]{C(6,"paused")});engine.Tick(2);
            Check(engine.X(visible)==x,"pause stays frozen while appending");
            engine.OnSeek(6,2);Check(engine.Active.Any(a=>a.Comment.Text=="paused") && engine.CommentCount==6,"seek includes appended history");
            var backlog=new CommentEngine(new());backlog.SetComments(Enumerable.Range(0,210272).Select(i=>C(i*.2,"x")).ToArray());
            backlog.SetPlaying(true,0);backlog.OnPosition(0,0);backlog.OnPosition(40000,100);
            Check(backlog.Dropped>190000 && backlog.Active.Count<=70,"large backlog skipped with binary search");

            // Exercise real filesystem notifications and disposal; no player/window is launched.
            var gate=new object();var received=new HashSet<string>();var errors=new List<string>();int callbacks=0;
            using(var session=new LiveCommentSession(ts,config,b=>{lock(gate){callbacks++;foreach(var c in b.Added)received.Add(c.Text);}},e=>{lock(gate)errors.Add(e);},CancellationToken.None))
            {
                bool Wait(string text)=>SpinWait.SpinUntil(()=>{lock(gate)return received.Contains(text);},TimeSpan.FromSeconds(6));
                Check(Wait("再作成"),"watcher initial read");
                Append(first,Encoding.UTF8.GetBytes("\n"+Chat(7,"通知追記")));Check(Wait("通知追記"),"watcher append");
                string rotated=Path.Combine(channel,(stamp+8)+".txt");File.WriteAllText(rotated,Chat(8,"通知切替"));Check(Wait("通知切替"),"watcher new log");
                session.Dispose();int stopped;lock(gate)stopped=callbacks;
                Append(rotated,Encoding.UTF8.GetBytes(Chat(9,"終了後")));Thread.Sleep(1200);
                lock(gate)Check(callbacks==stopped && errors.Count==0,"disposed watcher stops work");
            }
            foreach(bool together in new[]{false,true})
            {
                var sync=new object();var counts=new int[2];var texts=new[]{new HashSet<string>(),new HashSet<string>()};
                var failures=new List<string>();var pair=new LiveCommentSession[2];
                try
                {
                    for(int i=0;i<2;i++)
                    {
                        int index=i;
                        pair[i]=new(ts,config,b=>{lock(sync){counts[index]++;foreach(var c in b.Added)texts[index].Add(c.Text);}},
                            e=>{lock(sync)failures.Add(e);},CancellationToken.None);
                    }
                    bool Seen(int index,string text){lock(sync)return texts[index].Contains(text);}
                    Check(SpinWait.SpinUntil(()=>Seen(0,"再作成") && Seen(1,"再作成"),6000),"both sessions read the same TS");
                    string both=together?"同時終了前":"片方終了前";
                    Append(first,Encoding.UTF8.GetBytes(Chat(10,both)));
                    Check(SpinWait.SpinUntil(()=>Seen(0,both) && Seen(1,both),6000),"both sessions receive the same append");
                    if(together)Parallel.Invoke(()=>pair[0].Dispose(),()=>pair[1].Dispose());
                    else
                    {
                        pair[0].Dispose();int stopped;lock(sync)stopped=counts[0];
                        Append(first,Encoding.UTF8.GetBytes(Chat(11,"残った側のみ")));
                        Check(SpinWait.SpinUntil(()=>Seen(1,"残った側のみ"),6000),"surviving session continues after peer disposal");
                        lock(sync)Check(counts[0]==stopped && !texts[0].Contains("残った側のみ"),"disposed peer stays stopped");
                        pair[1].Dispose();
                    }
                    int[] before;lock(sync)before=counts.ToArray();
                    Append(first,Encoding.UTF8.GetBytes(Chat(12,together?"同時終了後":"両終了後")));Thread.Sleep(1200);
                    lock(sync)Check(counts.SequenceEqual(before) && failures.Count==0,"both sessions stay stopped without errors");
                    // Dispose may also be invoked again by player disconnect/exit cleanup.
                    Parallel.Invoke(()=>pair[0].Dispose(),()=>pair[1].Dispose());
                }
                finally{foreach(var item in pair)item?.Dispose();}
            }
            File.WriteAllText(Path.Combine(Paths.Logs,"live-comment-test.json"),JsonSerializer.Serialize(new {passed=true,playerUsed=false,
                cases=new[]{"incremental UTF-8 and multiline","log rotation/truncation","deduplication","TS growth and deferred comments","pause/seek","backlog binary search","filesystem notifications and disposal",
                    "two sessions sharing one TS and log","single-session disposal preserves peer","simultaneous and repeated disposal"}},Config.Json));
            return 0;
        }
        catch(Exception ex){File.WriteAllText(Path.Combine(Paths.Logs,"live-comment-test.json"),ex.ToString());return 1;}
    }
}
