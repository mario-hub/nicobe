using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace NicoBE;

internal static class PastLogTest
{
    static readonly List<string> checks=new();
    static void Check(bool condition,string name){if(!condition)throw new Exception(name);checks.Add(name);}
    sealed class Handler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> run):HttpMessageHandler
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>run(request,token);}
    static HttpResponseMessage Response(long second,string text="test")=>new(HttpStatusCode.OK)
    {Content=new StringContent($"<packet>\n<chat thread=\"lv1\" no=\"1\" date=\"{second}\" date_usec=\"2\" user_id=\"u\">{text}</chat>\n</packet>",Encoding.UTF8,"application/xml")};
    static SourceComment Chat(string thread="lv1",string user="u",long micro=2,string number="1",string text="test")=>
        new(1.000002,text,new(){["thread"]=thread,["no"]=number,["date"]="1600000001",["date_usec"]=micro.ToString(),["user_id"]=user});
    public static int Run(string? samples)
    {
        try
        {
            string root=Path.Combine(Paths.Root,"temp","test-results","pastlog-"+Guid.NewGuid().ToString("N")[..8]);Directory.CreateDirectory(root);
            CommentSceneTest.Verify(root,Check);
            var a=Chat("ch1_1600000000",micro:3);var b=Chat();
            Check(CommentSources.Merge(new[]{a},new[]{b},0).Count==1,"Known channel/live thread and 1us difference deduplicate");
            Check(CommentSources.Merge(new[]{a},new[]{b with {Text="other"}},0).Count==2,"Different text retained");
            Check(CommentSources.Merge(new[]{a},new[]{Chat(user:"other")},0).Count==2,"Different users retained");
            Check(CommentSources.Merge(new[]{a},new[]{Chat(micro:1)},0).Count==2,"No broad timestamp tolerance");
            Check(CommentSources.Merge(new[]{a},new[]{Chat(number:"2")},0).Count==2,"Distinct numbered posts retained");
            Check(CommentSources.Merge(new[]{Chat("unknown")},new[]{b},0).Count==2,"Unknown thread mappings retained");
            Check(CommentSources.Merge(Array.Empty<SourceComment>(),new[]{b},0).Single()==b,"Disabling local preserves remote copy");
            var chosen=new CommentSourceSelection();chosen.Automatic(true,true,true,false,"ASS");Check(chosen.Ass&&!chosen.Past,"Partial API retains ASS preference");
            chosen.Automatic(true,true,true,true,"ASS");Check(!chosen.Ass&&chosen.Saved&&chosen.Past,"Complete API supersedes ASS");
            chosen.Choose("ass",true);chosen.Automatic(true,true,true,true,"Original");Check(chosen.Ass&&!chosen.Saved&&!chosen.Past,"Manual ASS excludes raw sources and remains selected");
            chosen.Choose("past",true);Check(!chosen.Ass&&chosen.Past,"Manual raw source excludes ASS");
            var cache=new PastLogCache(Path.Combine(root,"cache"));var range=new PastLogRange(1600000000000000,1600000060000000);
            cache.Save("jk1",range,new[]{b},DateTimeOffset.UtcNow,CancellationToken.None);
            var read=cache.Read("jk1",range,range.Start,CancellationToken.None);
            Check(read.Complete&&read.Stable&&read.Comments.Count==1,"Old partial block persists as complete stable coverage");
            Check(Math.Abs(read.Comments[0].MediaSeconds-1.000002)<.0000001,"Microsecond synchronization");
            cache.Save("jk1",range,Array.Empty<SourceComment>(),DateTimeOffset.UtcNow,CancellationToken.None);
            read=cache.Read("jk1",range,range.Start,CancellationToken.None);Check(read.Stable&&read.Comments.Count==0,"Successful empty response replaces old data and covers range");
            var recent=new PastLogRange(CommentSources.Microseconds(DateTimeOffset.UtcNow.AddMinutes(-2)),CommentSources.Microseconds(DateTimeOffset.UtcNow.AddMinutes(-1)));
            cache.Save("jk1",recent,Array.Empty<SourceComment>(),DateTimeOffset.UtcNow,CancellationToken.None);
            var fresh=cache.Read("jk1",recent,recent.Start,CancellationToken.None);Check(fresh.Complete&&!fresh.Stable,"Recent empty response remains provisional");
            Check(cache.Read("jk1",new(range.Start,range.End+1000000),range.Start,CancellationToken.None).Missing(new(range.Start,range.End+1000000),true).Count==1,"Missing tail remains missing");
            bool rejected=false;try{PastLogCache.Parse(Encoding.UTF8.GetBytes("<!DOCTYPE packet [<!ENTITY x SYSTEM 'file:///x'>]><packet>&x;</packet>"),CancellationToken.None);}catch(System.Xml.XmlException){rejected=true;}
            Check(rejected,"External entities prohibited");
            var json=Directory.GetFiles(cache.Root,"*.json",SearchOption.AllDirectories).First();File.WriteAllText(json,"broken");
            Check(!cache.Read("jk1",range,range.Start,CancellationToken.None).Stable,"Broken manifest is not treated as fetched");
            string assFile=Path.Combine(root,"comments.ass");AssComments.Write(assFile,new[]{new Comment(1,"test","#FFFFFF",1,CommentPosition.Scroll)},CancellationToken.None);
            string ass=File.ReadAllText(assFile);Check(AssCommentClassifier.IsComment(ass),"NicoBE ASS recognized");
            Check(!AssCommentClassifier.IsComment(ass.Replace("Title: NicoBE Comments","Title: ordinary subtitles")),"Moving ordinary subtitle is not classified as commentary");
            Check(!AssCommentClassifier.IsComment(ass+"\nDialogue: 0,0:00:02.00,0:00:03.00,Ordinary,,0,0,0,,spoken words"),"Mixed ordinary styles rejected");
            var engine=new CommentEngine(new Config());engine.SetComments(new[]{new Comment(1,"old","#FFFFFF",1,CommentPosition.Scroll)});engine.OnSeek(20,0);
            engine.UpdateComments(new[]{new Comment(1,"old","#FFFFFF",1,CommentPosition.Scroll),new Comment(19,"late","#FFFFFF",1,CommentPosition.Scroll)});
            Check(engine.Active.Count==0,"API late arrivals are not replayed immediately");engine.OnSeek(20,1);Check(engine.Active.Any(c=>c.Comment.Text=="late"),"Late comment becomes available on seek");
            var sent=new List<(uint,string)>();var subtitles=new SubtitleControl((command,value)=>{sent.Add((command,value));return true;});
            subtitles.SetSource(new(new[]{"NicoBE Comments"},false));subtitles.Poll(true,0);subtitles.Receive("Japanese|0");Check(!sent.Any(c=>c.Item1==MpcClient.SetSubtitleTrack),"Ordinary subtitle not disabled");
            subtitles.Receive("NicoBE Comments|0");Check(sent.Any(c=>c==(MpcClient.SetSubtitleTrack,"-1")),"Identified commentary subtitle disabled");
            subtitles.Receive("NicoBE Comments|-1");subtitles.Poll(false,2);subtitles.Receive("NicoBE Comments|-1");Check(!sent.Any(c=>c==(MpcClient.SetSubtitleTrack,"0")),"Master OFF does not restore commentary subtitle");
            subtitles.Restore();Check(sent.Last()==(MpcClient.SetSubtitleTrack,"0"),"Session release restores original subtitle");
            NetworkTests(root).GetAwaiter().GetResult();
            if(samples!=null)
            {
                var local=PastLogCache.Parse(File.ReadAllBytes(Path.Combine(samples,"local-pair-nicojk.xml")),CancellationToken.None);
                var api=PastLogCache.Parse(File.ReadAllBytes(Path.Combine(samples,"local-pair.xml")),CancellationToken.None);
                int combined=CommentSources.Merge(local,api,0).Count;
                Check(local.Count==112&&api.Count==113&&combined==113,"Actual NicoJK/API 112+113 records merge to 113");
            }
            Paths.WriteJson("pastlog-test.json",new{passed=true,checks,root,network="mocked",realPlayerControlled=false});return 0;
        }
        catch(Exception ex){Paths.WriteJson("pastlog-test.json",new{passed=false,checks,error=ex.ToString()});return 1;}
    }
    static async Task NetworkTests(string root)
    {
        long start=1600000000000000;var range=new PastLogRange(start,start+60_000_000);int calls=0;
        var ready=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cache=new PastLogCache(Path.Combine(root,"network"));
        var service=new PastLogService(cache,()=>new(new Handler(async(_,token)=>{Interlocked.Increment(ref calls);ready.TrySetResult();await release.Task.WaitAsync(token);return Response(start/1000000+1);})),TimeSpan.Zero);
        using var firstCancel=new CancellationTokenSource();var first=service.FetchAsync("jk1",range,start,false,firstCancel.Token);
        await ready.Task;var second=service.FetchAsync("jk1",range,start,false,CancellationToken.None);firstCancel.Cancel();
        bool canceled=false;try{await first;}catch(OperationCanceledException){canceled=true;}
        release.SetResult();var result=await second;
        Check(canceled&&calls==1&&result.Snapshot.Stable,"One consumer cancel keeps shared fetch alive for the other");
        await service.FetchAsync("jk1",range,start,false,CancellationToken.None);Check(calls==1,"Stable cache performs no network request");
        var separate=new PastLogService(cache,()=>new(new Handler((_,_)=>{Interlocked.Increment(ref calls);return Task.FromResult(Response(start/1000000+1));})),TimeSpan.Zero);
        await separate.FetchAsync("jk1",range,start,false,CancellationToken.None);Check(calls==1,"Independent service reuses disk cache");
        var config=new Config{NicoJkLogFolder=""};config.ChannelOverrides["1:1"]="jk1";
        var info=new RecordingInfo("test.ts",1,1,0,DateTimeOffset.FromUnixTimeSeconds(start/1000000),DateTimeOffset.FromUnixTimeSeconds(range.End/1000000),0);
        var warnings=new List<string>();var prepared=await PastLogEncoder.PrepareAsync(info,config,true,warnings,CancellationToken.None,separate);
        Check(calls==1&&prepared.SourceComments?.Count==1&&warnings.Count==0,"Encoder reuses cache without a NicoJK folder");
        var parallelCache=new PastLogCache(Path.Combine(root,"parallel"));int parallelCalls=0;
        var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var finish=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PastLogService Independent()=>new(parallelCache,()=>new(new Handler(async(_,token)=>{Interlocked.Increment(ref parallelCalls);started.TrySetResult();await finish.Task.WaitAsync(token);return Response(start/1000000+1);})),TimeSpan.Zero);
        var left=Independent().FetchAsync("jk1",range,start,false,CancellationToken.None);await started.Task;
        var right=Independent().FetchAsync("jk1",range,start,false,CancellationToken.None);finish.SetResult();await Task.WhenAll(left,right);
        Check(parallelCalls==1,"Independent concurrent services serialize and recheck the cache");
        int recentCalls=0;long now=CommentSources.Microseconds(DateTimeOffset.UtcNow.AddMinutes(-2));var recentRange=new PastLogRange(now,now+30_000_000);
        var recent=new PastLogService(new(Path.Combine(root,"recent")),()=>new(new Handler((_,_)=>{recentCalls++;return Task.FromResult(Response(now/1000000+1));})),TimeSpan.Zero);
        await recent.FetchAsync("jk1",recentRange,now,false,CancellationToken.None);
        var again=await recent.FetchAsync("jk1",recentRange,now,false,CancellationToken.None);
        Check(recentCalls==1&&again.RetryUtc>DateTimeOffset.UtcNow,"Recent fetch button is limited across requests");
        int errors=0;var failed=new PastLogService(new(Path.Combine(root,"errors")),()=>new(new Handler((_,_)=>{errors++;return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));})),TimeSpan.Zero);
        var error=await failed.FetchAsync("jk1",range,start,false,CancellationToken.None);await failed.FetchAsync("jk1",range,start,false,CancellationToken.None);
        Check(errors==1&&!error.Snapshot.Complete&&error.Warning!=null,"Failure is not cached as success and backs off");
        var denied=new PastLogService(new(Path.Combine(root,"denied")),()=>new(new Handler((_,_)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)))),TimeSpan.Zero);
        Check((await denied.FetchAsync("jk1",range,start,false,CancellationToken.None)).Permanent,"403 stops automatic retry");
    }
}
