using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NicoBE.Encoder;

public static class CommentFormatTest
{
    public static int Run()
    {
        string folder=Path.Combine(EncoderPaths.Root,"temp", "test-results","comment-format");Directory.CreateDirectory(folder);
        void Check(bool condition,string name){if(!condition)throw new Exception(name);}
        void Reject(Action action,string name)
        {try{action();}catch(Exception ex) when(ex is InvalidDataException or JsonException or EndOfStreamException){return;}throw new Exception(name);}
        var token=CancellationToken.None;
        Check(EncoderPaths.Root==StorageLayout.Current.EncoderDirectory
            && EncoderPaths.Settings==Path.Combine(EncoderPaths.Root,"encoder-settings.json")
            && EncoderPaths.Logs==Path.Combine(EncoderPaths.Root,"logs")
            && EncoderPaths.Output==Path.Combine(EncoderPaths.Root,"output"),"encoder settings/logs/output layout");
        long stamp=1790000000;
        string logs=Path.Combine(folder,"logs"),channel=Path.Combine(logs,"jk4");Directory.CreateDirectory(channel);
        string body="  一行目 & < >\n二行目\n\t三行目  ";
        string xml=$"<chat date=\"{stamp}\" date_usec=\"001234\" thread=\"0001\" no=\"0002\" vpos=\"000123\" user_id=\"id-test\" mail=\"184 ue big mincho #aAbBcC unknown\" premium=\"1\" anonymity=\"1\" future=\"保持&amp;値\">  一行目 &amp; &lt; &gt;&#10;二行目\n&#9;三行目  </chat>";
        File.WriteAllText(Path.Combine(channel,$"{stamp}.txt"),xml+"\n"+xml+"\n"
            +$"<chat date=\"{stamp+1}\" deleted=\"1\">削除済み</chat>\n"
            +$"<chat date=\"{stamp+2}\">/info test</chat>\n"
            +$"<chat date=\"{stamp+3}\">{new string('あ',1100)}</chat>\n"
            +$"<chat date=\"{stamp+4}\"></chat>\n",new UTF8Encoding(false));
        var config=new Config {NicoJkLogFolder=logs};config.ChannelOverrides["15:1040"]="jk4";
        var start=DateTimeOffset.FromUnixTimeSeconds(stamp);
        var prepared=LogReader.Prepare(new("fixture.ts",1040,15,0,start,start.AddSeconds(10),0),config,token,true);
        Check(prepared.Malformed==0 && prepared.SourceComments?.Count==6,"raw records including duplicate/deleted/command/long/empty");
        var first=prepared.SourceComments![0];
        Check(first.Text==body && first.Attributes["date_usec"]=="001234" && first.Attributes["future"]=="保持&値"
            && first.Attributes["no"]=="0002" && first.Attributes["mail"]=="184 ue big mincho #aAbBcC unknown","original values");
        var payload=new Mp4CommentData(1,"jk4",prepared.SourceComments);
        string path=Path.Combine(folder,"comments-only.mp4");
        // Valid top-level ftyp box; comment reading does not require decoding media.
        File.WriteAllBytes(path,new byte[]{0,0,0,16,(byte)'f',(byte)'t',(byte)'y',(byte)'p',(byte)'i',(byte)'s',(byte)'o',(byte)'m',0,0,0,0});
        payload.Append(path,token);var restored=Mp4CommentData.TryRead(path,token)!;
        Check(payload.ContentEquals(restored),"lossless text/attribute round trip");
        var playback=LogReader.Prepare(path,new Config {NicoJkLogFolder=""},token);
        Check(playback.Comments.Count==2 && playback.Comments[0].Text==body && playback.Comments[0].Position==CommentPosition.Top
            && playback.Comments[0].SizeScale==1.3 && playback.Comments[0].Color=="#aAbBcC","no metadata/logs; newline and mail playback");
        var metadata=new Mp4RecordingMetadata(1,start,start,1040,15,"fixture.ts",10,100,0);
        metadata.Append(path);Check(Mp4RecordingMetadata.Read(path,token)==metadata,"recording metadata round trip");
        Check(payload.ContentEquals(Mp4CommentData.TryRead(path,token)!),"comments alongside metadata");
        byte[] saved=File.ReadAllBytes(path);
        Reject(()=>payload.Append(path,token),"duplicate append accepted");Check(saved.SequenceEqual(File.ReadAllBytes(path)),"append rejection modified input");
        string duplicate=Path.Combine(folder,"duplicate.mp4");File.WriteAllBytes(duplicate,saved.Concat(saved.Skip(16)).ToArray());
        Reject(()=>Mp4CommentData.TryRead(duplicate,token),"duplicate read accepted");
        string malformed=Path.Combine(folder,"malformed.mp4");File.WriteAllBytes(malformed,saved.Concat(new byte[]{0}).ToArray());
        Reject(()=>Mp4CommentData.TryRead(malformed,token),"trailing truncation accepted");
        Reject(()=>new Mp4CommentData(2,null,new()).Validate(),"unknown version accepted");
        Reject(()=>Mp4UuidBox.Deserialize<Mp4CommentData>(Encoding.UTF8.GetBytes("{\"Version\":1,\"Version\":1,\"Comments\":[]}")),"duplicate JSON property accepted");
        Reject(()=>Mp4UuidBox.Deserialize<Mp4CommentData>(Encoding.UTF8.GetBytes("{\"Version\":1,\"Comments\":[{\"MediaSeconds\":0,\"Text\":\"x\"}]}")),"missing attributes accepted");
        var extensible=Mp4UuidBox.Deserialize<Mp4CommentData>(Encoding.UTF8.GetBytes("{\"Version\":1,\"Future\":true,\"Comments\":[]}"));extensible.Validate();
        Check(extensible.Channel==null,"optional channel");
        string ass=Path.Combine(folder,"multiline.ass");AssComments.Write(ass,new[]{playback.Comments[0]},token);
        Check(AssComments.ReadFile(ass,token)[0].Text.Contains("\n二行目\n"),"ASS newline round trip");
        Check(System.Text.RegularExpressions.Regex.IsMatch(LicensePanel.Version,@"^\d+\.\d+\.\d+$") && LicensePanel.ReadResource("LICENSE").Contains("MIT License")
            && LicensePanel.ReadResource("THIRD-PARTY-NOTICES").Contains("GPLv3"),"version and embedded notices");
        var license=LicensePanel.Create("NicoBE Encoder");var view=(FrameworkElement)license.Content;
        SettingsTheme.Apply(new Window {Content=view});
        view.Measure(new Size(820,650));view.Arrange(new Rect(0,0,820,650));view.UpdateLayout();
        var image=new RenderTargetBitmap(820,650,96,96,PixelFormats.Pbgra32);image.Render(view);
        var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(image));using(var output=File.Create(Path.Combine(folder,"license.png")))png.Save(output);
        File.WriteAllText(Path.Combine(folder,"result.json"),JsonSerializer.Serialize(new {passed=true,sourceRecords=restored.Comments.Count,displayed=playback.Comments.Count,
            originalTextAndAttributes=true,multiline=true,metadataIndependent=true,invalidDataRejected=true,version=LicensePanel.Version}));
        return 0;
    }
}
