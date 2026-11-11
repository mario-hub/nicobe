using System.Globalization;
using System.IO;
using System.Text.Json;

namespace NicoBE.Encoder;

internal static class AssStyleTest
{
    internal static int Run()
    {
        var checks=new List<string>();
        void Check(bool value,string message){if(!value)throw new Exception(message);checks.Add(message);}
        string folder=Path.Combine(EncoderPaths.Temp,"test-results","ass-style");Directory.CreateDirectory(folder);
        try
        {
            var config=new Config();var defaults=new EncoderSettings().AssStyle;
            Check(config.FontFamily==defaults.FontFamily&&config.FontWeight==defaults.FontWeight&&config.OutlineLevel==defaults.OutlineLevel
                &&config.ShadowLevel==defaults.ShadowLevel&&config.FontSizePercent==defaults.FontSizePercent&&config.ScrollSpeedPercent==defaults.ScrollSpeedPercent
                &&config.LineSpacingPercent==defaults.LineSpacingPercent&&config.AccelerateLongComments==defaults.AccelerateLongComments
                &&config.Opacity*100==defaults.OpacityPercent&&config.FixedCommentSeconds==defaults.FixedCommentSeconds
                &&config.MaxVisibleComments==defaults.MaxVisibleComments&&config.AdvanceSeconds==defaults.AdvanceSeconds,"player and encoder share all ASS-capable defaults");
            var style=new AssStyleSettings {FontFamily="Arial",FontWeight=900,OutlineLevel=4,ShadowLevel=5,FontSizePercent=150,ScrollSpeedPercent=200,
                LineSpacingPercent=25,AccelerateLongComments=false,OpacityPercent=80,FixedCommentSeconds=7,AdvanceSeconds=1,MaxVisibleComments=90};
            var panel=new AssStylePanel();panel.Load(style);
            Check(JsonSerializer.Serialize(panel.Read())==JsonSerializer.Serialize(style),"GUI reads every ASS field without dropping values");
            panel.SetAssEnabled(false);Check(JsonSerializer.Serialize(panel.Read())==JsonSerializer.Serialize(style),"disabled ASS controls retain their values");
            var settings=new EncoderSettings{AssStyle=style};string profile=Path.Combine(folder,"settings.json");settings.Save(profile,false);
            var restored=EncoderSettings.Load(profile);
            Check(JsonSerializer.Serialize(restored.AssStyle)==JsonSerializer.Serialize(style),"Encoder JSON roundtrips all ASS settings");
            Check(!File.ReadAllText(profile).Contains("CommentSizePercent"),"new settings store size only inside AssStyle");
            string legacy=Path.Combine(folder,"legacy.json");File.WriteAllText(legacy,"{\"CommentSizePercent\":135}");
            var migrated=EncoderSettings.Load(legacy);
            Check(migrated.AssStyle.FontSizePercent==135&&migrated.AssStyle.FontFamily==CommentDisplayDefaults.FontFamily,"old size-only settings migrate without changing the selected size");
            string ass=Path.Combine(folder,"custom.ass");var token=CancellationToken.None;
            var comments=new[]{new Comment(2,"FIRST","#FF0000",1,CommentPosition.Scroll),new Comment(2,"SECOND","#FFFFFF",1,CommentPosition.Scroll)};
            Check(AssComments.Write(ass,comments,token,3,restored.AssStyle)==2,"custom style exports both comments");
            string document=File.ReadAllText(ass);string[] fields=File.ReadLines(ass).First(l=>l.StartsWith("Style:")).Split(',');
            Check(fields[1]=="Arial"&&fields[3]=="&H33FFFFFF"&&fields[16]=="6"&&fields[17]=="6","font opacity outline and shadow reach the ASS style");
            Check(document.Contains(@"\b900")&&document.Contains(@"\move(1920,5,480,5)")&&document.Contains(@"\move(1920,95,480,95)"),"weight speed acceleration OFF and line spacing reach ASS events");
            Check(document.Contains("Dialogue: 0,0:00:01.00,0:00:03.00"),"time adjustment advances ASS timestamps");
            Check(AssComments.ReadFile(ass,token).All(c=>Math.Abs(c.SizeScale-1.5)<.001),"selected font metrics and size read back correctly");
            style.MaxVisibleComments=1;
            Check(AssComments.Write(ass,comments,token,3,style)==1,"simultaneous comment limit is applied");
            style.AccelerateLongComments=true;
            AssComments.Write(ass,comments.Take(1).ToArray(),token,3,style);
            Check(File.ReadAllText(ass).Contains(@"\move(1920,5,336,5)"),"length acceleration affects the saved movement");
            AssComments.Write(ass,new[]{comments[0] with{Position=CommentPosition.Top}},token,20,style);
            Check(File.ReadAllText(ass).Contains("Dialogue: 0,0:00:01.00,0:00:08.00"),"fixed comment duration is applied");
            style.AccelerateLongComments=false;style.AdvanceSeconds=3;
            AssComments.Write(ass,comments.Take(1).ToArray(),token,3,style);
            Check(File.ReadAllText(ass).Contains("Dialogue: 0,0:00:00.00")&&File.ReadAllText(ass).Contains(@"\move(1200,5,"),"advancing before zero clips time without restarting the comment at the edge");
            var bad=new AssStyleSettings{FontFamily="bad,style\n"};bool rejected=false;
            try{bad.Validate();}catch(ArgumentException){rejected=true;}Check(rejected,"invalid ASS font names cannot inject script fields");
            Paths.WriteJson("ass-style-test.json",new{passed=true,checks});return 0;
        }
        catch(Exception error){Paths.WriteJson("ass-style-test.json",new{passed=false,checks,error=error.ToString()});return 1;}
    }
}
