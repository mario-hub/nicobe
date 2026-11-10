namespace NicoBE;

internal static class CommentSpeedTest
{
    internal static void Verify(Action<bool,string> check)
    {
        Comment Chat(double at,string text,CommentPosition position=CommentPosition.Scroll)=>new(at,text,"#FFFFFF",1,position);
        string longText=new('あ',60);
        foreach(var (length,multiplier) in new[]{(0,1d),(1,1.02),(10,1.2),(20,1.4),(49,1.98),(50,2d),(60,2d),(100,2d)})
            check(Math.Abs(CommentEngine.LengthSpeedMultiplier(new string('あ',length))-multiplier)<1e-9,$"length speed boundary: {length}");
        check(CommentEngine.LengthSpeedMultiplier(new string('あ',20)+"\r\n"+new string('い',15))==1.4,"multiline speed uses the longest line, not the total");
        check(CommentEngine.LengthSpeedMultiplier(string.Concat(Enumerable.Repeat("👩‍💻か\u3099",5)))==1.2,"emoji and combining marks count as visible characters");

        CommentEngine Pair(double delay,bool enabled=true,bool fastFirst=false)
        {
            var engine=new CommentEngine(new() {AccelerateLongComments=enabled});
            engine.Measure=_=>(100,40,null);
            engine.SetComments(new[]{Chat(0,fastFirst?longText:"短い"),Chat(delay,fastFirst?"短い":longText)});
            engine.OnSeek(delay,0);return engine;
        }
        var catching=Pair(1);
        check(catching.Active.Count==2 && catching.Active[0].Lane!=catching.Active[1].Lane,"a faster follower cannot enter a lane where it would catch up");
        var safe=Pair(3);
        check(safe.Active.Count==2 && safe.Active[0].Lane==safe.Active[1].Lane,"a faster follower reuses a lane when the leader exits in time");
        var leader=safe.Active[0];var follower=safe.Active[1];
        double right=safe.X(leader)+leader.Width;
        check(new[]{0d,.25,.5,.75,1d}.All(fraction=>
        {
            double time=right/leader.Velocity*fraction;
            return safe.X(follower)-follower.Velocity*time-(right-leader.Velocity*time)>=35;
        }),"same-lane comments retain a gap until the leader fully exits");
        var slow=Pair(1,fastFirst:true);
        check(slow.Active[0].Lane==slow.Active[1].Lane,"a slower follower safely reuses the lane");
        var disabled=Pair(1,enabled:false);
        check(disabled.Active.All(a=>a.Velocity==360) && disabled.Active[0].Lane==disabled.Active[1].Lane,"disabled acceleration restores uniform speed and lane reuse");

        var crowded=new CommentEngine(new() {FontSize=144,LineSpacingPercent=100});
        crowded.Measure=_=>(100,40,null);
        crowded.SetComments(new[]{Chat(0,"一"),Chat(0,"二"),Chat(0,"三"),Chat(1,longText)});crowded.OnSeek(1,0);
        check(crowded.Active.Count==3 && crowded.Dropped==1 && crowded.Active.All(a=>a.Comment.Text!=longText),"no safe lane drops the whole comment without delaying it");
        var multi=new CommentEngine(new());multi.Measure=c=>(100,c.Text.Contains('\n')?130:40,null);
        multi.SetComments(new[]{Chat(0,"上\n中\n下"),Chat(1,longText)});multi.OnSeek(1,0);
        check(multi.Active.Count==2 && multi.Active[1].Lane>=multi.Active[0].LaneSpan,"catch-up prevention covers every row of multiline comments");
        var fixedComments=new CommentEngine(new());fixedComments.Measure=_=>(100,40,null);
        fixedComments.SetComments(new[]{Chat(0,longText,CommentPosition.Top),Chat(0,longText,CommentPosition.Bottom)});fixedComments.OnSeek(2,0);
        check(fixedComments.Active.All(a=>a.Velocity==0 && fixedComments.X(a)==910),"top and bottom comments stay fixed");

        foreach(double rate in new[]{.5,1,2,3})
        {
            var engine=new CommentEngine(new());engine.Measure=_=>(100,40,null);
            engine.SetComments(new[]{Chat(0,longText)});engine.OnSeek(0,0);engine.SetPlaying(true,0);
            var active=engine.Active.Single();double start=engine.X(active);
            for(int i=1;i<=10;i++)engine.OnPosition(i*.1*rate,i*.1);
            check(active.Velocity==720 && Math.Abs(start-engine.X(active)-720)<1e-6,$"accelerated speed remains independent of playback at {rate}x");
            engine.SetPlaying(false,1);double paused=engine.X(active);engine.Tick(2);
            check(engine.X(active)==paused,$"accelerated comments freeze while paused at {rate}x");
            engine.OnSeek(2,2);var restored=engine.Active.Single();
            check(restored.Velocity==720 && engine.X(restored)==480,$"seek uses the accelerated speed at {rate}x");
            engine.OnSeek(4,2);check(engine.Active.Count==0,$"seek omits accelerated comments that have exited at {rate}x");
        }
        var config=new Config();var live=new CommentEngine(config);live.Measure=_=>(100,40,null);
        live.SetComments(new[]{Chat(0,longText)});live.OnSeek(2,0);config.AccelerateLongComments=false;live.RefreshSettings();
        check(live.Active.Single().Velocity==360 && live.X(live.Active.Single())==1200,"turning acceleration off applies to active comments");
        config.AccelerateLongComments=true;live.RefreshSettings();
        check(live.Active.Single().Velocity==720 && live.X(live.Active.Single())==480,"turning acceleration on reconstructs active comments");
    }
}
