using System.IO;

namespace NicoBE;

public static class AssPlaybackTest
{
    public static int Run()
    {
        var checks = new List<string>();
        void Check(bool value, string name) { if (!value) throw new Exception(name); checks.Add(name); }
        try
        {
            const string ass = "[Script Info]\nPlayResY: 1080\n[V4+ Styles]\nFormat: Name, Fontsize, PrimaryColour, Alignment\nStyle: Default,38.4,&H00FFFFFF,8\n[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\nDialogue: 0,0:00:01.25,0:00:07.00,Default,,0,0,0,,{\\move(1920,20,-100,20)\\c&H0000FF&}流れる,コメント\nDialogue: 0,0:00:02.00,0:00:05.00,Default,,0,0,0,,上固定\\N次の行\nDialogue: 0,0:00:03.00,0:00:06.00,Default,,0,0,0,,{\\an2}下固定";
            var comments = AssComments.Read(ass, CancellationToken.None);
            Check(comments.Count == 3 && comments[0].MediaSeconds == 1.25 && comments[0].Text == "流れる,コメント", "ASS timestamps and commas");
            Check(comments[0].Position == CommentPosition.Scroll && comments[0].Color == "#FF0000", "ASS movement and BGR color");
            Check(comments[1].Position == CommentPosition.Top && comments[1].Text == "上固定\n次の行" && comments[2].Position == CommentPosition.Bottom, "ASS alignment and line breaks");
            string folder = Path.Combine(Paths.Root, "temp", "test-results", "ass-layout");
            Directory.CreateDirectory(folder);
            string exported = Path.Combine(folder, "layout.ass");
            var input = new List<Comment> {
                new(0, "日本語コメント", "#FFFFFF", 1, CommentPosition.Scroll),
                new(0, "大きなコメント", "#FFFFFF", 1.5, CommentPosition.Scroll),
                new(2, "続くコメント", "#FFFFFF", 1, CommentPosition.Scroll),
                new(3, "この歌あってる？", "#FFFFFF", 1, CommentPosition.Top),
                new(4, "下固定コメント", "#FFFFFF", 1, CommentPosition.Bottom)
            };
            AssComments.Write(exported, input, CancellationToken.None);
            var roundTrip = AssComments.ReadFile(exported, CancellationToken.None);
            Check(roundTrip.Count == input.Count && input.Zip(roundTrip).All(pair => Math.Abs(pair.First.SizeScale - pair.Second.SizeScale) < .001), "ASS font correction preserves NicoBE sizes");
            var events = File.ReadLines(exported).Where(l => l.StartsWith("Dialogue:")).ToArray();
            Check(events[2].Contains(@"\move(1920,5,"), "Reuse a scrolling row after the previous text clears the entrance");
            Check(events[1].Contains(@"\move(1920,67.4,"), "Rows follow 48px font and 30 percent spacing");
            Check(events[4].Contains(@"\an2\pos(960,1067.244)"), "Bottom comments follow the default bottom lane");
            string clipped = Path.Combine(folder, "clipped.ass");
            AssComments.Write(clipped, input.Take(1).ToArray(), CancellationToken.None, 1);
            Check(File.ReadAllText(clipped).Contains(@"\move(1920,5,1509.6,5)"), "End-of-video clipping preserves 360px per second plus length acceleration");
            Check(File.ReadAllText(exported).Contains("Yu Gothic UI,63.844,&H0DFFFFFF") && events[0].Contains(@"\fs63.844"),"Export uses default font size and opacity");
            string variants=Path.Combine(folder,"variants.ass");
            Comment Chat(double at,string text)=>new(at,text,"#FFFFFF",1,CommentPosition.Scroll);
            AssComments.Write(variants,new[]{Chat(0,"a"),Chat(.8,new string('あ',50))},CancellationToken.None,1.8);
            var accelerated=File.ReadLines(variants).Where(l=>l.StartsWith("Dialogue:")).ToArray();
            Check(accelerated[1].Contains(@"\move(1920,67.4,1200,67.4)"),"Fast following comment moves at 720px per second and cannot catch the leading row");
            var dense=Enumerable.Range(0,18).Select(i=>Chat(0,"同時"+i)).Append(Chat(10,"空いた行" )).ToArray();
            int count=AssComments.Write(variants,dense,CancellationToken.None);
            var sparse=AssComments.ReadFile(variants,CancellationToken.None);
            Check(count==18 && sparse.Count==18 && sparse[^1].Text=="空いた行" && !sparse.Any(c=>c.Text=="同時17"),"Crowding drops a comment instead of overlapping and later reuses released lanes");
            Check(AssComments.Write(variants,new[]{Chat(0,string.Join("\n",Enumerable.Repeat("行",30)))},CancellationToken.None)==0,"Oversized multiline comment is omitted");
            AssComments.Write(variants,new[]{Chat(0,"サイズ")},CancellationToken.None,5,150);
            Check(Math.Abs(AssComments.ReadFile(variants,CancellationToken.None)[0].SizeScale-1.5)<.001,"New export size is applied once when read by NicoBE");
            string legacy=ass.Replace("Style: Default,38.4","Style: NicoBE_Em_1.330078125,51.075").Replace(",Default,",",NicoBE_Em_1.330078125,");
            Check(Math.Abs(AssComments.Read(legacy,CancellationToken.None)[0].SizeScale-1)<.001,"Legacy export size stays unchanged");
            var sent = new List<(uint Command, string Value)>();
            var control = new SubtitleControl((command,value) => { sent.Add((command,value)); return true; });
            control.SetSource(new(new[] { "NicoBE Comments" }, false));
            control.Poll(true, 0);control.Receive("Japanese|NicoBE Comments|1");
            Check(sent.Contains((MpcClient.SetSubtitleTrack,"-1")), "Disable selected comment track");
            control.Poll(false, 1);control.Receive("Japanese|NicoBE Comments|-1");
            Check(sent[^1] == (MpcClient.SetSubtitleTrack,"1"), "Restore original track on hide");
            control.Reset();sent.Clear();control.SetSource(new(new[] { "NicoBE Comments" }, false));
            control.Poll(true, 2);control.Receive("Japanese|NicoBE Comments|0");
            Check(!sent.Any(s => s.Command == MpcClient.SetSubtitleTrack), "Leave selected dialogue subtitles enabled");
            control.Receive("Japanese|NicoBE Comments|1");control.Receive("Japanese|NicoBE Comments|0");
            int before=sent.Count;control.Restore();
            Check(sent.Count==before, "Respect a manual subtitle selection");
            Paths.WriteJson("ass-test.json", new { passed=true, checks });return 0;
        }
        catch(Exception error){Paths.WriteJson("ass-test.json",new{passed=false,checks,error=error.ToString()});return 1;}
    }
}
