namespace NicoBE;

public static class PastLogEncoder
{
    public static async Task<PreparedComments> PrepareAsync(RecordingInfo recording,Config config,bool useApi,
        ICollection<string> warnings,CancellationToken token,PastLogService? service=null)
    {
        string channel=LogReader.ResolveChannel(recording,config);var local=new List<SourceComment>();int files=0,malformed=0;
        if(!string.IsNullOrWhiteSpace(config.NicoJkLogFolder))
        {
            try
            {
                var loaded=await Task.Run(()=>LogReader.Prepare(recording,config,token,true),token).ConfigureAwait(false);
                local=loaded.SourceComments??new();files=loaded.Files;malformed=loaded.Malformed;
            }
            catch(CommentLogUnavailableException){}
        }
        var remote=new List<SourceComment>();long zero=CommentSources.Microseconds(recording.Start);
        if(useApi)
        {
            service??=PastLogService.Shared;
            try
            {
                var result=await service.FetchAsync(channel,PastLogRange.For(recording),zero,false,token).ConfigureAwait(false);
                remote=result.Snapshot.Comments;
                if(result.Warning!=null)warnings.Add("過去ログの補完に失敗した範囲があります: "+result.Warning);
                if(!result.Snapshot.Complete)warnings.Add("過去ログの未取得範囲があります。取得済みのコメントで出力しました。");
                else if(!result.Snapshot.Stable)warnings.Add("直近の過去ログは収集途中の可能性があります。現時点のコメントで出力しました。");
            }
            catch(OperationCanceledException) when(token.IsCancellationRequested){throw;}
            catch(Exception ex) when(ex is System.IO.IOException or UnauthorizedAccessException or System.Net.Http.HttpRequestException)
            {
                warnings.Add("過去ログの補完に失敗しました: "+ex.Message);
                remote=service.Cache.Read(channel,PastLogRange.For(recording),zero,token).Comments;
            }
        }
        var combined=CommentSources.Merge(local,remote,zero);
        if(combined.Count>500000)throw new System.IO.InvalidDataException("保存するコメントが50万件の上限を超えました。");
        return new(recording,channel,combined.Select(LogReader.ForPlayback).Where(c=>c!=null).Cast<Comment>().ToList(),files,malformed){SourceComments=combined};
    }
}
