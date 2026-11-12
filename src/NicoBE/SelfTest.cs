namespace NicoBE;

public static class SelfTest
{
    public static int ReadRecording(string path)
    {
        try
        {
            var result=LogReader.Prepare(path,Config.Load(false),CancellationToken.None);
            Paths.WriteJson("recording-test.json",new{passed=true,result.Channel,comments=result.Comments.Count,result.Files,result.Recording,
                firstSeconds=result.Comments.FirstOrDefault()?.MediaSeconds,lastSeconds=result.Comments.LastOrDefault()?.MediaSeconds,result.Subtitle});return 0;
        }
        catch(Exception ex){Paths.WriteJson("recording-test.json",new{passed=false,error=ex.ToString()});return 1;}
    }
    public static int Run()
    {
        var results=new List<string>();
        void Check(bool value,string message){if(!value)throw new Exception(message);results.Add(message);}
        try
        {
            StorageLayoutTest.Verify(Check);
            Mp4UuidBoxTest.Verify(Check);
            CommentSpeedTest.Verify(Check);
            PlaybackSpeedMemoryTest.VerifyAsync(Check).GetAwaiter().GetResult();
            foreach(double rate in new[]{.5,1,2})
            {
                var config=new Config {AccelerateLongComments=false};
                var applied=new CommentEngine(config);int measured=0;
                applied.Measure=_=>{measured++;return (100,40,new object());};
                applied.SetComments(new[]{new Comment(0,"反映テスト","#FFFFFF",1,CommentPosition.Scroll)});
                applied.OnPosition(0,0);applied.SetPlaying(true,0);
                var original=applied.Active.Single();
                for(int i=1;i<=5;i++)
                {
                    applied.OnPosition(i*.2*rate,i*.2);
                    double x=applied.X(original),motion=applied.MotionSeconds;
                    applied.RefreshSettings();
                    Check(ReferenceEquals(applied.Active.Single(),original) && applied.X(original)==x
                        && applied.MotionSeconds==motion && measured==1,$"unchanged apply preserves scene at {rate}x, click {i}");
                }
                applied.SetPlaying(false,1);
                double paused=applied.X(original);
                config.PlaybackPanelVisible=false;config.AutoFetchPastLogs=true;
                applied.RefreshSettings();applied.Tick(2);applied.RefreshSettings();
                Check(ReferenceEquals(applied.Active.Single(),original) && applied.X(original)==paused,
                    $"unrelated settings and repeated paused apply preserve scene at {rate}x");
                config.FontSize=60;applied.RefreshSettings();
                var resized=applied.Active.Single();
                Check(!ReferenceEquals(resized,original),"changed layout is applied");
                applied.RefreshSettings();Check(ReferenceEquals(applied.Active.Single(),resized),"second apply after layout change is a no-op");
                applied.RefreshSettings(force:true);
                Check(!ReferenceEquals(applied.Active.Single(),resized),"visibility and GPU recovery can explicitly reconstruct the scene");
            }
            foreach(double rate in new[]{.5,1,1.5,2,3})
            {
                var e=new CommentEngine(new() {AccelerateLongComments=false});
                e.SetComments(new[]{new Comment(.01,"速度テスト","#FFFFFF",1,CommentPosition.Scroll)});
                e.OnPosition(0,0);e.SetPlaying(true,0);
                for(int i=1;i<=10;i++)e.OnPosition(i*.02*rate,i*.02);
                var a=e.Active.Single();double x=e.X(a);
                for(int i=11;i<=60;i++)e.OnPosition(i*.02*rate,i*.02);
                Check(Math.Abs((x-e.X(a))-new Config().ScrollPixelsPerSecond)<.001,$"constant scroll at {rate}x");
                e.SetPlaying(false,1.2);double frozen=e.X(a);
                e.OnPosition(1.2*rate,2.2);Check(Math.Abs(e.X(a)-frozen)<.001,$"pause at {rate}x");
                e.OnSeek(.1,2.2);Check(e.Active.All(item=>!ReferenceEquals(item,a)) && e.Active.Count==1,$"seek reconstructs comments at {rate}x");
            }
            foreach(double spacing in new[]{0d,50d,60d})
            {
                var single=new CommentEngine(new() {FontSize=46.08,LineSpacingPercent=spacing});
                single.Measure=c=>(100,46.08*1.330078125*c.SizeScale,null);
                single.SetComments(new[]{new Comment(1,"通常","#FFFFFF",1,CommentPosition.Top),new Comment(1,"大文字","#FFFFFF",1.3,CommentPosition.Top),new Comment(1,"次","#FFFFFF",1,CommentPosition.Top)});
                single.OnSeek(1,0);
                Check(single.Active.Count==3 && single.Active.All(a=>a.LaneSpan==1)
                    && single.Active.Select(a=>a.Lane).SequenceEqual(new[]{0,1,2})
                    && Math.Abs(single.Y(single.Active[1])-single.Y(single.Active[0])-single.LaneHeight)<.0001,
                    $"single-line comments keep configured spacing at {spacing} percent");
            }
            var multiline=new CommentEngine(new());
            multiline.Measure=c=>(100,c.Text.Contains('\n')?140:40,null);
            multiline.SetComments(new[]{new Comment(1,"上\n下","#FFFFFF",1,CommentPosition.Top),new Comment(1,"次","#FFFFFF",1,CommentPosition.Top)});
            multiline.OnSeek(1,0);
            Check(multiline.Active.Count==2 && multiline.Active[0].LaneSpan>=3 && multiline.Active[1].Lane>=multiline.Active[0].LaneSpan,"multiline comments reserve their measured height");
            // Reproduce both orders of the initial position and background load.
            var opening=new[]{0d,1d,2d,4d}.Select(t=>new Comment(t,t.ToString(),"#FFFFFF",1,CommentPosition.Scroll)).ToArray();
            foreach(bool loadFirst in new[]{false,true})
            {
                var initial=new CommentEngine(new() {AccelerateLongComments=false});initial.Measure=_=>(100,40,null);
                if(loadFirst)initial.SetComments(opening);
                initial.SetPlaying(true,0);initial.OnPosition(2.5,0);
                if(!loadFirst)initial.SetComments(opening);
                Check(initial.Active.Select(a=>a.Comment.MediaSeconds).SequenceEqual(new[]{0d,1d,2d}),$"initial history restored with loadFirst={loadFirst}");
                Check(Math.Abs(initial.X(initial.Active[0])-(1920-2.5*new Config().ScrollPixelsPerSecond))<.001,"initial history has aged screen positions");
                initial.OnPosition(2.5,.1);
                Check(initial.Active.Count==3,"initial history is not duplicated by position updates");
                initial.OnPosition(4,.2);
                Check(initial.Active.Count(a=>a.Comment.MediaSeconds==4)==1,"future comment follows restored history once");
                initial.Reset();initial.SetComments(new[]{opening[0]});initial.OnPosition(.5,1);
                Check(initial.Active.Count==1 && !initial.Playing,"new file restores initial history while paused");
            }
            var emptyLoad=new CommentEngine(new());emptyLoad.OnPosition(2.5,0);
            emptyLoad.AppendComments(Array.Empty<Comment>());emptyLoad.AppendComments(opening);
            Check(emptyLoad.Active.Count==3,"first nonempty live batch restores initial history while paused");
            var backlog=new CommentEngine(new());backlog.Measure=_=>(600,40,null);
            backlog.OnPosition(100,0);backlog.SetPlaying(true,0);
            backlog.SetComments(new[]{0d,50d,89d,95d,100d,101d}.Select(t=>new Comment(t,"a","#FFFFFF",1,CommentPosition.Scroll)).ToArray());
            backlog.OnPosition(100.2,.1);
            Check(backlog.Active.Count==2 && backlog.Active.All(a=>a.Comment.MediaSeconds>=90),"background load restores recent history without replaying old backlog");
            Check(MpcClient.SplitFields(@"title|author|desc|D:\test.ts|123")[3]==@"D:\test.ts","API backslashes preserved");
            Check(MpcClient.SplitFields(@"a\|b|c")[0]=="a|b","API escaped separator");
            Check(TsReader.PcrDelta(20,8589934590)==22,"PCR wraparound");
            byte[] command=MpcClient.CommandLinePacket("/slave","12345");
            Check(BitConverter.ToUInt32(command)==2 && System.Text.Encoding.Unicode.GetString(command,4,command.Length-4)=="/slave\0"+"12345\0","MPC command-line packet format");
            var delayed=new CommentEngine(new());delayed.OnPosition(0,0);delayed.SetPlaying(true,0);
            delayed.Tick(.4);Check(Math.Abs(delayed.MotionSeconds-.4)<.0001,"GPU clock does not rewind after delayed UI tick");
            delayed.Tick(1);Check(Math.Abs(delayed.MotionSeconds-.65)<.0001,"GPU clock freezes when API lease expires");
            var seek=new CommentEngine(new() {ScrollPixelsPerSecond=240,AccelerateLongComments=false});seek.Measure=c=>(600,40,null);
            seek.SetComments(new[]{89d,90d,91d,95d,99d,100d,101d}.Select(t=>new Comment(t,t.ToString(),"#FFFFFF",1,CommentPosition.Scroll)).ToArray());
            seek.OnSeek(100,0);
            Check(seek.Active.Count==5 && seek.Active.Min(a=>a.Comment.MediaSeconds)==90 && seek.Active.Max(a=>a.Comment.MediaSeconds)==100,"seek includes preceding ten seconds only");
            Check(Math.Abs(seek.X(seek.Active.Single(a=>a.Comment.MediaSeconds==95))-720)<.001,"seek restores aged positions instead of a burst at the right edge");
            double pausedX=seek.X(seek.Active[0]);seek.Tick(.5);Check(Math.Abs(seek.X(seek.Active[0])-pausedX)<.001,"seek history is visible and frozen while paused");
            seek.OnPosition(100,.5);Check(seek.Active.Count==5,"restored comments are not duplicated by position updates");
            var loading=new CommentEngine(new());loading.OnSeek(100,0);loading.SetComments(new[]{new Comment(95,"復元","#FFFFFF",1,CommentPosition.Scroll)});
            Check(loading.Active.Count==1,"seek while preparing restores history when loading completes");
            var timing=new Config {AdvanceSeconds=3};var early=new CommentEngine(timing);
            early.SetComments(new[]{new Comment(103,"早める","#FFFFFF",1,CommentPosition.Scroll)});early.OnSeek(100,0);
            Check(early.Active.Count==1 && Math.Abs(early.X(early.Active[0])-1920)<.001,"positive time adjustment displays comments earlier");
            Check(new Config().FontSizePercent==100 && new Config().ScrollSpeedPercent==100,"size and speed default to one hundred percent");
            var ngConfig=new Config {NgIds=new(){" UserA ","UserA",""},NgWords=new(){"Spoiler","spoiler"," "}};
            ngConfig.Validate(false);var filter=new NgFilter(ngConfig);
            Comment Chat(double at,string text,string id="")=>new(at,text,"#FFFFFF",1,CommentPosition.Scroll,id);
            Check(ngConfig.NgIds.Count==1 && ngConfig.NgWords.Count==1,"NG entries remove blank lines and duplicates");
            Check(filter.Blocks(Chat(0,"normal","UserA")) && !filter.Blocks(Chat(0,"normal","usera")) && !filter.Blocks(Chat(0,"normal","UserAB")),"NG IDs are case-sensitive exact matches");
            Check(filter.Blocks(Chat(0,"a SPOILER here")) && !filter.Blocks(Chat(0,"normal")),"NG words use case-insensitive substrings and missing IDs remain eligible");
            var ngEngine=new CommentEngine(ngConfig);
            ngEngine.SetComments(new[]{Chat(99,"normal","UserA"),Chat(100,"normal","UserB"),Chat(101,"SPOILER","UserC")});ngEngine.OnSeek(100,0);
            Check(ngEngine.Active.Count==1 && ngEngine.Active[0].Comment.UserId=="UserB","NG comments are excluded from seek restoration");
            var snapshot=ngEngine.NearbyComments(100);ngConfig.NgIds.Add("UserB");ngEngine.RefreshSettings();
            Check(ngEngine.Active.Count==0 && snapshot.Count==3,"saved NG changes remove visible comments while log snapshots retain blocked comments");
            ngConfig.NgIds.Clear();ngConfig.NgWords.Clear();ngEngine.RefreshSettings();
            Check(ngEngine.Active.Count==2,"removing NG entries restores recent eligible comments without reloading logs");
            var around=new CommentEngine(new Config {AdvanceSeconds=2});
            around.SetComments(new[]{Chat(96.9,"before"),Chat(97,"start"),Chat(102,"center"),Chat(107,"end"),Chat(107.1,"after")});
            Check(around.NearbyComments(100).Select(r=>r.Text).SequenceEqual(new[]{"start","center","end"}),"log snapshot includes both five-second boundaries and accounts for time adjustment");
            around.OnSeek(500,1);Check(snapshot.Count==3 && snapshot[0].Seconds==99,"captured log rows remain fixed after playback moves");
            var savedNg=System.Text.Json.JsonSerializer.Deserialize<Config>(System.Text.Json.JsonSerializer.Serialize(new Config {NgIds=new(){"id"},NgWords=new(){"語"}},Config.Json),Config.Json)!;
            Check(savedNg.Clone().NgIds.Single()=="id" && savedNg.NgWords.Single()=="語","NG settings survive serialization and cloning");
            var variants=new[]{Chat(1,"red") with {Color="#FF0000"},Chat(1,"top") with {Position=CommentPosition.Top},
                Chat(1,"big") with {SizeScale=1.3},Chat(1,"line\nbreak")};
            Check(variants.All(c=>!new NgFilter(new Config()).Blocks(c)),"all four category filters default OFF");
            var switches=new Action<Config,bool>[] {(c,v)=>c.NgColoredComments=v,(c,v)=>c.NgFixedComments=v,
                (c,v)=>c.NgSizeChangedComments=v,(c,v)=>c.NgMultilineComments=v};
            for(int i=0;i<switches.Length;i++)
            {
                var cfg=new Config();switches[i](cfg,true);var categoryFilter=new NgFilter(cfg.Clone());
                Check(variants.Select(categoryFilter.Blocks).SequenceEqual(Enumerable.Range(0,4).Select(n=>n==i))
                    && !categoryFilter.Blocks(Chat(1,"normal")),"category filter is independent and persists: "+i);
                var categoryEngine=new CommentEngine(cfg);categoryEngine.SetComments(new[]{variants[i]});categoryEngine.OnSeek(1,0);
                Check(categoryEngine.Active.Count==0,"category NG applies on initial seek: "+i);
                switches[i](cfg,false);categoryEngine.RefreshSettings();Check(categoryEngine.Active.Count==1,"category NG OFF restores comments: "+i);
                switches[i](cfg,true);categoryEngine.RefreshSettings();Check(categoryEngine.Active.Count==0,"category NG ON removes active comments: "+i);
            }
            var allCategories=new NgFilter(new Config {NgColoredComments=true,NgFixedComments=true,NgSizeChangedComments=true,NgMultilineComments=true});
            Check(allCategories.Blocks(Chat(1,"bottom") with {Position=CommentPosition.Bottom})
                && allCategories.Blocks(Chat(1,"small") with {SizeScale=.8}) && allCategories.Blocks(Chat(1,"a\rb"))
                && !allCategories.Blocks(Chat(1,"😀") with {Color="#ffffff",SizeScale=1.0000001}),"bottom small CR line breaks and white emoji boundaries");
            Paths.WriteJson("self-test.json",new{passed=true,results});return 0;
        }
        catch(Exception ex){Paths.WriteJson("self-test.json",new{passed=false,results,error=ex.ToString()});return 1;}
    }
}
