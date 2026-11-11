using System.IO;
using System.Text;

namespace NicoBE;

internal static class AssCompatibilityTest
{
    // Synthetic comments in the output structure observed in NicoConvAss 1.51t and
    // rigaya/Amatsukaze scripts/nicojk_ass.py fixup_ass. No downloaded program code.
    internal static string Sample()
    {
        string[] colors={"white","red","pink","orange","yellow","green","cyan","blue","purple","black"};
        string[] rgb={"ffffff","0000ff","9314ff","008cff","00ffff","008000","ffff00","ff0000","800080","000000"};
        var s=new StringBuilder("[Script Info]\nTitle: example\nScriptType: v4.00+\nPlayResX: 1920\nPlayResY: 1080\n[V4+ Styles]\nFormat: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding\n");
        for(int i=0;i<colors.Length;i++)s.AppendLine($"Style: {colors[i]},MS PGothic,28,&H00{rgb[i]},&H00{rgb[i]},&H00000000,&H00000000,-1,0,0,0,200,200,0,0.00,1,0,4,7,20,20,40,1");
        s.AppendLine("[Events]\nFormat: Layer, Start, End, Style, Actor, MarginL, MarginR, MarginV, Effect, Text");
        s.AppendLine(@"Dialogue: 0,0:00:01.00,0:00:07.00,white,,0000,0000,0000,,{\move(1920,9,-336,9)}流れるコメント");
        s.AppendLine(@"Dialogue: 0,0:00:04.00,0:00:10.00,red,,0000,0000,0000,,{\move(875,9,875,9)}上固定");
        s.AppendLine(@"Dialogue: 0,0:00:07.00,0:00:13.00,blue,,0000,0000,0000,,{\move(875,1014,875,1014)}下固定");
        return s.ToString();
    }

    public static void Run(Action<bool,string> check,string? folder)
    {
        string sample=Sample();
        const string footer=@"Dialogue: 0,23:59:58.00,23:59:59.00,white,,0000,0000,0000,,{\move(0,0,0,0)}[NicoConvAssData:delay=-1random1sec=0:]";
        check(AssCommentClassifier.IsComment(sample),"NicoConvAss without optional footer is recognized");
        check(AssCommentClassifier.IsComment(sample+footer),"NicoConvAss with optional footer is recognized");
        var comments=AssComments.Read(sample+footer,CancellationToken.None);
        check(comments.Count==3&&comments[^1].MediaSeconds==7,"Management footer excluded from comments and timeline");
        check(comments.Select(c=>c.Position).SequenceEqual(new[]{CommentPosition.Scroll,CommentPosition.Top,CommentPosition.Bottom}),"Stationary move becomes top/bottom fixed comment");
        check(comments.All(c=>Math.Abs(c.SizeScale-1)<.001),"NicoConvAss standard size maps to player 100 percent");
        var scaled=AssComments.Read(sample.Replace(@"\move(1920",@"\fscy150\move(1920"),CancellationToken.None);
        check(Math.Abs(scaled[0].SizeScale-.75)<.001,"Inline vertical scale remains relative to standard style");
        var big=AssComments.Read(sample.Replace(@"\move(",@"\fs42\move("),CancellationToken.None);
        check(big.All(c=>Math.Abs(c.SizeScale-1.5)<.001),"Known style wins even when every sampled comment is big");
        check(AssComments.Read(sample.Replace("PlayResY: 1080","PlayResY: 720"),CancellationToken.None)
            .All(c=>Math.Abs(c.SizeScale-1)<.001),"Script resolution does not change normalized standard size");
        const string unknown="[Script Info]\nPlayResY: 1080\n[V4+ Styles]\nFormat: Name, Fontsize, ScaleY\nStyle: A,20,200\nStyle: B,60,100\n[Events]\nFormat: Start, End, Style, Text\n";
        string Event(int second,string style,string tag="")=>$"Dialogue: 0:00:{second:00},0:10:00,{style},{{{tag}}}comment{second}\n";
        var inferred=AssComments.Read(unknown+Event(1,"B")+Event(2,"A")+Event(3,"A")+Event(4,"A",@"\fs10"),CancellationToken.None);
        check(inferred.Select(c=>c.SizeScale).SequenceEqual(new[]{1.5,1d,1d,.5}),"Ambiguous styles use effective-size mode and retain big/small ratios");
        string many=unknown+string.Concat(Enumerable.Range(0,150).Reverse().Select(i=>
            $"Dialogue: 0:{i/60:00}:{i%60:00},0:10:00,{(i<60?"A":"B")},comment{i}\n"));
        var first100=AssComments.Read(many,CancellationToken.None);
        check(first100.Count==150&&first100[0].SizeScale==1&&first100[^1].SizeScale==1.5,"Fallback uses first 100 by playback time, not file order or whole file");
        var noStyle=AssComments.Read("[Events]\nFormat: Start, End, Style, Text\n"+Event(1,"missing",@"\fs56")+Event(2,"missing",@"\fs56")+Event(3,"missing",@"\fs84"),CancellationToken.None);
        check(noStyle.Select(c=>c.SizeScale).SequenceEqual(new[]{1d,1d,1.5}),"Missing style can infer standard from inline sizes");
        var tied=AssComments.Read(unknown+Event(1,"A")+Event(2,"B"),CancellationToken.None);
        check(tied[0].SizeScale==1&&tied[1].SizeScale==1.5,"Mode ties are deterministic");
        var repeated=AssComments.Read(unknown+string.Concat(Enumerable.Repeat(Event(1,"B"),5))+Event(2,"A")+Event(3,"A"),CancellationToken.None);
        check(repeated.Count==3&&repeated[0].SizeScale==1.5,"Duplicates do not bias the standard-size sample");
        string fixedOnly=string.Join("\n",sample.Split('\n').Where(l=>!l.Contains("流れるコメント")));
        check(AssCommentClassifier.IsComment(fixedOnly),"Fixed-only NicoConvAss is recognized");
        string ama=sample.Replace("Title: example\n","").Replace(",200,200,",",100,100,").Replace("Actor,","Name,")
            .Replace(@"\move(875,9,875,9)",@"\an8\pos(960,9)").Replace(@"\move(875,1014,875,1014)",@"\an2\pos(960,1014)");
        check(AssCommentClassifier.IsComment(ama),"Amatsukaze Python output without generator header is recognized");
        check(AssComments.Read(ama,CancellationToken.None)[2].Position==CommentPosition.Bottom,"Amatsukaze bottom pos alignment retained");
        check(AssCommentClassifier.IsComment(sample.Replace("&H00ffffff,&H00ffffff","&H70ffffff,&H30ffffff")),"Independently configured primary/secondary opacity accepted");
        string ordinary="[Script Info]\nTitle: NicoJK-1080S\nPlayResX: 1920\nPlayResY: 1080\n[V4+ Styles]\nFormat: Name, Fontsize, Alignment\nStyle: Default,40,2\n[Events]\nFormat: Start, End, Style, Text\nDialogue: 0:00:01.00,0:00:06.00,Default,{\\move(1920,50,-100,50)}通常字幕";
        check(!AssCommentClassifier.IsComment(ordinary),"NicoJK title plus movement alone never identifies ordinary subtitles");
        check(!AssCommentClassifier.IsComment(ordinary+"\n"+footer),"Footer alone never identifies ordinary subtitles");
        check(!AssCommentClassifier.IsComment(sample.Replace("Style: cyan,","Style: other,")),"Incomplete known palette is rejected");
        check(!AssCommentClassifier.IsComment(sample+"Dialogue: 0,0:00:01.00,0:00:06.00,Default,,0,0,0,,通常字幕"),"Mixed ordinary dialogue style is rejected");
        check(!AssCommentClassifier.IsComment(sample.Replace(@"{\move(1920,9,-336,9)}", "")),"Unpositioned ordinary dialogue within colored styles is rejected");
        check(!AssCommentClassifier.IsComment(sample.Replace(@"\move(1920,9,-336,9)",@"\move(0,9,1920,9)")),"Unrecognized rightward animation is rejected");
        var control=new SubtitleControl((cmd,value)=>{if(cmd==MpcClient.SetSubtitleTrack)throw new Exception("Ordinary subtitle changed");return true;});
        control.SetSource(new(new[]{"NicoJK-1080S"},false));control.Poll(true,0);control.Receive("ASS|NicoJK-720S|NicoJK-1080S|0");
        check(!control.CommentDisplayedByPlayer,"Amatsukaze normal ASS track stays selected");
        var sent=new List<(uint,string)>();
        var variants=new SubtitleControl((cmd,value)=>{sent.Add((cmd,value));return true;});
        variants.SetSource(new(new[]{"NicoJK-1080S","NicoJK-720S"},false));variants.Poll(true,0);
        variants.Receive("ASS|NicoJK-720S|NicoJK-1080S|1");
        check(sent.Contains((MpcClient.SetSubtitleTrack,"-1")),"Other verified commentary variant is suppressed without selecting normal subtitles");
        variants.Reset();sent.Clear();variants.SetSource(new(new[]{"ASS"},false));variants.Poll(true,1);
        variants.Receive("ASS|ASS commentary|0");
        check(!sent.Any(s=>s.Item1==MpcClient.SetSubtitleTrack),"Ambiguous subtitle names never suppress normal tracks");
        variants.Reset();sent.Clear();variants.SetSource(new(new[]{"NicoJK-1080S"},false));variants.Poll(true,10);
        variants.Receive("NicoJK-1080S|ASS|SRT|0");
        check(variants.AwaitingSuppression,"Wait briefly for subtitle-off acknowledgement");
        variants.Receive("NicoJK-1080S|ASS|SRT|0");
        check(variants.AwaitingSuppression,"Queued old selection does not cancel pending subtitle suppression");
        variants.Poll(true,10.6);
        check(!variants.AwaitingSuppression&&!variants.CommentDisplayedByPlayer,"Missing off reply cannot hide MKV commentary indefinitely");
        variants.Poll(true,11.1);
        check(sent.Count(s=>s.Item1==MpcClient.GetSubtitleTracks)==2,"Continue polling after missing acknowledgement");
        variants.Restore();check(sent[^1]==(MpcClient.SetSubtitleTrack,"0"),"Original subtitle selection restored even without off reply");
        variants.Reset();sent.Clear();variants.SetSource(new(new[]{"NicoJK-1080S"},false));variants.Poll(true,20);
        variants.Receive("NicoJK-1080S|ASS|SRT|0");variants.Receive("NicoJK-1080S|ASS|SRT");
        check(!variants.AwaitingSuppression&&!variants.CommentDisplayedByPlayer,"Known subtitle list without index acknowledges subtitle off");
        variants.Receive("NicoJK-1080S|ASS|SRT|1");int beforeRestore=sent.Count;variants.Restore();
        check(sent.Count==beforeRestore,"Manual normal subtitle selection is preserved after off acknowledgement");
        variants.Reset();sent.Clear();variants.SetSource(new(new[]{"NicoJK-1080S"},false));variants.Poll(true,30);variants.Poll(true,32.1);
        check(!variants.AwaitingSuppression&&!sent.Any(s=>s.Item1==MpcClient.SetSubtitleTrack),"Missing initial list never hides overlay indefinitely or guesses a subtitle to disable");
        var titles=new[]{"ASS","NicoJK-720T","NicoJK-1080T","NicoJK-720S","NicoJK-1080S"};
        check(titles.OrderBy(AssCommentClassifier.TrackPriority).First()=="NicoJK-1080S","Deterministic single high-resolution commentary variant");
        string prefixFolder=Path.Combine(Paths.Root,"temp","test-results","ass-prefix-"+Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(prefixFolder);
        string video=Path.Combine(prefixFolder,"ニュース_20261110.mp4"),exact=Path.ChangeExtension(video,".ass");
        string prefixed=Path.Combine(prefixFolder,"ニュース_20261110-hoge.ass");
        File.WriteAllText(prefixed,sample);
        File.WriteAllText(Path.Combine(prefixFolder,"別のニュース_20261110.ass"),sample);
        check(new Config().MatchExternalAssPrefix,"Prefix ASS matching defaults to ON");
        check(ContainerComments.ExternalFiles(video).SequenceEqual(new[]{prefixed})&&ContainerComments.External(video,CancellationToken.None)?.Comments.Count==3,"Arbitrary basename prefix ASS is loaded without matching unrelated names");
        check(ContainerComments.ExternalFiles(video,false).Length==0&&ContainerComments.External(video,CancellationToken.None,false)==null,"Prefix matching OFF skips suffixed ASS");
        File.WriteAllText(exact,fixedOnly);
        check(ContainerComments.External(video,CancellationToken.None)?.Comments.Count==2,"Exact ASS wins over prefixed ASS even for fixed comments only");
        check(ContainerComments.External(video,CancellationToken.None,false)?.Comments.Count==2,"Exact basename ASS remains usable when prefix matching is OFF");
        File.WriteAllText(exact,ordinary);
        check(ContainerComments.External(video,CancellationToken.None)?.Comments.Count==3,"Unrecognized ordinary subtitle does not prevent commentary fallback");
        File.WriteAllText(prefixed,ordinary);
        check(ContainerComments.External(video,CancellationToken.None)==null,"Prefix matching never treats normal subtitles as commentary");
        if(folder==null)return;
        // Optional actual generator outputs and tiny mixed-track MKV, prepared outside tests.
        string actual=File.ReadAllText(Path.Combine(folder,"fixed.ass"));
        check(AssCommentClassifier.IsComment(actual),"Actual NicoConvAss 1.51t output recognized");
        var parsed=AssComments.Read(actual,CancellationToken.None);
        check(parsed.Count==4&&parsed[1].Position==CommentPosition.Top&&parsed[2].Position==CommentPosition.Bottom,"Actual generated fixed/scroll comments parsed");
        string withFooter=File.ReadAllText(Path.Combine(folder,"sample.ass"));
        check(AssCommentClassifier.IsComment(withFooter)&&AssComments.Read(withFooter,CancellationToken.None).Count==4,"Actual management footer ignored");
        var config=new Config{NicoJkLogFolder=""};
        var mixed=CommentMediaContext.Load(Path.Combine(folder,"mixed.mkv"),config,CancellationToken.None);
        check(mixed.Ass.Count==4&&mixed.Subtitle?.Names.SequenceEqual(new[]{"NicoJK-1080S","NicoJK-720S"})==true,"Native MKV reader skips normal subtitles, selects one scene and identifies only verified commentary variants");
        var normal=CommentMediaContext.Load(Path.Combine(folder,"normal.mkv"),config,CancellationToken.None);
        check(normal.Ass.Count==0&&normal.Subtitle==null,"Normal-only MKV never supplies commentary or subtitle control");
        var external=CommentMediaContext.Load(Path.Combine(folder,"external.mp4"),config,CancellationToken.None);
        check(external.Ass.Count==4,"Actual external ASS accepted through playback source loading");
    }
}
