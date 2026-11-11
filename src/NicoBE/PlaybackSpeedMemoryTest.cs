using System.Globalization;
using System.Text.Json;

namespace NicoBE;

internal static class PlaybackSpeedMemoryTest
{
    internal static async Task VerifyAsync(Action<bool,string> check)
    {
        check(new Config().KeepPlaybackSpeed && JsonSerializer.Deserialize<Config>("{}")!.KeepPlaybackSpeed,
            "new and existing settings default to speed inheritance on");
        string status="再生中 (1.75x)";int reads=0;var sent=new List<double>();var warnings=new List<string>();
        TaskCompletionSource<string>? pending=null;
        using var memory=new PlaybackSpeedMemory(speed=>
        {sent.Add(speed);status=$"Playing ({speed.ToString(CultureInfo.InvariantCulture)}x)";return Task.FromResult(true);},warnings.Add,
            _=>{reads++;return pending?.Task??Task.FromResult(status);});
        await memory.TickAsync(1,0);check(reads==0,"no file means no rate polling");
        memory.FileLoaded("a.ts");await memory.TickAsync(1,0);
        check(memory.Remembered==1.75 && sent.Count==0,"attach learns MPC rate without changing it");
        await memory.TickAsync(1,.1);check(reads==1,"rate polling is throttled to half a second");
        memory.BeginFileChange();status="Playing";await memory.TickAsync(1,1);
        check(reads==1 && memory.Remembered==1.75,"loading never overwrites the previous rate with one");
        memory.FileLoaded("b.ts");await memory.TickAsync(1,1);await memory.TickAsync(1,1.5);
        check(sent.SequenceEqual(new[]{1.75}) && memory.Remembered==1.75,"restore and verify rate on the next file");
        status="Playing (1.4x)";await memory.TickAsync(1,2);
        memory.FileLoaded("c.ts");status="Playing";await memory.TickAsync(1,2.5);await memory.TickAsync(1,3);
        check(sent.Last()==1.4,"MPC rate outside the panel presets is inherited");
        memory.BeginFileChange();memory.FileLoaded("d.ts");status="Paused";
        await memory.TickAsync(1,4);await memory.TickAsync(1,20);
        check(warnings.Count==0 && sent.Count==2,"paused/loading status waits without forcing playback or warning");
        status="Playing";await memory.TickAsync(1,21);await memory.TickAsync(1,21.5);
        check(sent.Count==3 && memory.Remembered==1.4,"restore resumes when the new file reports a readable rate");
        pending=new(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldRead=memory.TickAsync(1,22);
        memory.BeginFileChange();memory.FileLoaded("e.ts");
        pending.SetResult("Playing (2.5x)");await oldRead;pending=null;
        check(memory.Remembered==1.4,"late reads from the previous file are discarded");
        status="Playing (1.25x)";await memory.TickAsync(1,23);
        check(memory.Remembered==1.25 && sent.Count==3,"new-file manual MPC rate supersedes the remembered rate");
        memory.BeginFileChange();memory.FileLoaded("f.ts");
        memory.UserCommandStarted();int before=reads;await memory.TickAsync(1,24);
        memory.UserSpeedChanged(2);memory.UserCommandFinished();status="Playing (2x)";
        await memory.TickAsync(1,24.5);
        check(reads==before+1 && memory.Remembered==2 && sent.Count==3,"manual panel command cancels automatic restore and suspends reads");
        memory.BeginFileChange();memory.FileLoaded("g.ts");memory.Enabled=false;
        before=reads;await memory.TickAsync(1,25);
        check(reads==before && memory.Remembered==null && sent.Count==3,"disabled inheritance stops polling and pending restore");
        memory.Enabled=true;status="Playing (0.5x)";await memory.TickAsync(1,26);
        check(memory.Remembered==.5 && sent.Count==3,"enabling learns current rate without changing playback");
        using var other=new PlaybackSpeedMemory(_=>throw new Exception("unexpected send"),warnings.Add,_=>Task.FromResult("Playing (2x)"));
        other.FileLoaded("g.ts");await other.TickAsync(2,0);
        check(other.Remembered==2 && memory.Remembered==.5,"players keep independent rates even for the same file");
        memory.Reset();memory.FileLoaded("h.ts");status="Playing";await memory.TickAsync(1,27);
        check(memory.Remembered==1 && sent.Count==3,"disconnect discards the old player rate");
        memory.PlaybackEnded();await memory.TickAsync(1,27.5);
        memory.PlaybackResumed();status="Playing (1.5x)";await memory.TickAsync(1,27.6);
        check(memory.Remembered==1.5 && sent.Count==3,"replaying the same file resumes speed observation");
        pending=new(TaskCreationOptions.RunContinuationsAsynchronously);oldRead=memory.TickAsync(1,28.2);
        memory.Dispose();pending.SetResult("Playing (2x)");await oldRead;
        check(memory.Remembered==null,"shutdown ignores outstanding reads");
        await other.TickAsync(2,1);check(other.Remembered==2,"closing one session leaves the other intact");

        foreach(bool delivered in new[]{false,true})
        {
            int tries=0,errors=0;string text="Playing (1.5x)";
            using var failing=new PlaybackSpeedMemory(_=>{tries++;return Task.FromResult(delivered);},_=>errors++, _=>Task.FromResult(text));
            failing.FileLoaded("a.ts");await failing.TickAsync(1,0);
            failing.BeginFileChange();failing.FileLoaded("b.ts");text="Playing";
            for(int i=1;i<=8;i++)await failing.TickAsync(1,i);
            check(tries==3 && errors==1,$"failed restore is bounded and reported even when delivery={delivered}");
        }
    }
}
