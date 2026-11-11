using System.IO;

namespace NicoBE;

internal sealed record CommentMediaContext(RecordingInfo? Recording,string? Channel,bool Growing,
    List<SourceComment> Saved,List<Comment> Ass,AssSubtitle? Subtitle,string? Unavailable)
{
    public List<SourceComment> Embedded {get;init;}=new();
    public bool Eligible=>Recording!=null&&Channel!=null;
    public static CommentMediaContext Load(string path,Config config,CancellationToken token)
    {
        RecordingInfo? recording=null;string? channel=null,unavailable=null;bool growing=false;
        var saved=new List<SourceComment>();var embedded=new List<SourceComment>();var ass=new List<Comment>();AssSubtitle? subtitle=null;
        string ext=Path.GetExtension(path).ToLowerInvariant();
        if(ext is ".ts" or ".m2ts" or ".mts")
        {growing=true;recording=TsReader.Read(path,config.ServiceId,token);}
        else if(ext==".mp4")
        {
            var data=Mp4CommentData.TryRead(path,token);if(data!=null)embedded=data.Comments;
            try{recording=Mp4RecordingMetadata.Read(path,token).Recording(path);}
            catch(InvalidDataException){unavailable="放送時刻・チャンネル情報がありません。";}
        }
        else if(ext==".mkv")
        {
            var (tag,count,tracks)=MkvReader.Read(path,token);
            recording=ContainerComments.RecordingMetadata(tag)?.Recording(path);
            var commentary=tracks.Where(t=>t.Document!=null&&AssCommentClassifier.IsComment(t.Document.ToString()))
                .OrderBy(t=>AssCommentClassifier.TrackPriority(t.Title)).ToArray();
            var names=commentary.Where(t=>t.Title.Length>0&&tracks.Count(other=>other.Title==t.Title)==1).Select(t=>t.Title).ToArray();
            foreach(var track in commentary)
            {
                ass=AssComments.Read(track.Document!.ToString(),token);
                if(ass.Count==0)continue;
                subtitle=new(names,count==1);break;
            }
        }
        if(ass.Count==0&&ext is ".mp4" or ".mkv" or ".ts" or ".m2ts" or ".mts")
        {
            foreach(string file in ContainerComments.ExternalFiles(path,config.MatchExternalAssPrefix))
            {
                if(new FileInfo(file).Length>PastLogClient.MaxBytes)continue;
                string doc=File.ReadAllText(file);token.ThrowIfCancellationRequested();
                if(!AssCommentClassifier.IsComment(doc))continue;
                ass=AssComments.Read(doc,token);if(ass.Count==0)continue;
                subtitle=new(new[]{Path.GetFileName(file),Path.GetFileNameWithoutExtension(file)},false);break;
            }
        }
        if(recording!=null)
        {
            try{channel=LogReader.ResolveChannel(recording,config);}
            catch(CommentLogUnavailableException ex){unavailable=ex.Message;}
            if(!growing&&channel!=null&&!string.IsNullOrWhiteSpace(config.NicoJkLogFolder))
            {
                try
                {
                    saved=LogReader.Prepare(recording,config,token,true).SourceComments??new();
                }
                catch(CommentLogUnavailableException){}
            }
        }
        return new(recording,channel,growing,saved,ass,subtitle,unavailable??(recording==null?"放送時刻・チャンネル情報がありません。":null)){Embedded=embedded};
    }
}
