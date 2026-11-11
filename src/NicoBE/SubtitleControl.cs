namespace NicoBE;

// One instance per MPC-BE session. Track changes made by the user take precedence.
public sealed class SubtitleControl
{
    readonly Func<uint, string, bool> send;
    AssSubtitle? source;
    string[] tracks = Array.Empty<string>();
    int savedIndex = -1, active = -1;
    bool enabled, userOverride;
    double lastQuery = double.NegativeInfinity;
    double lastWall, waitUntil=double.NaN;
    public bool CommentDisplayedByPlayer {get;private set;}
    public bool AwaitingSuppression {get;private set;}

    public SubtitleControl(Func<uint, string, bool> send) => this.send = send;
    public void Reset()
    {
        source = null; tracks = Array.Empty<string>(); savedIndex = -1; active = -1;
        userOverride = false; lastQuery = double.NegativeInfinity;
        waitUntil=double.NaN;
        CommentDisplayedByPlayer=AwaitingSuppression=false;
    }
    public void SetSource(AssSubtitle? value) { source = value; lastQuery = double.NegativeInfinity;AwaitingSuppression=value!=null;waitUntil=double.NaN; }
    public void Poll(bool visible, double wall)
    {
        enabled = visible;
        lastWall=wall;
        if(AwaitingSuppression)
        {
            if(double.IsNaN(waitUntil))waitUntil=wall+2;
            if(wall>=waitUntil)
            {
                // Some MPC-BE versions stop replying after selecting -1. The verified
                // commentary-disable request was delivered; absence of an acknowledgement must
                // not keep the entire overlay hidden. Continue polling for user changes.
                AwaitingSuppression=false;waitUntil=double.NaN;
                if(savedIndex>=0)CommentDisplayedByPlayer=false;
                TraceLog.Write("subtitle-response-timeout: resume commentary overlay");
            }
        }
        if (source == null || wall - lastQuery < 1) return;
        lastQuery = wall;
        send(MpcClient.GetSubtitleTracks, "");
    }
    public void Receive(string value)
    {
        var fields = MpcClient.SplitFields(value);
        string[] current;int selected;
        if(value=="-1"){current=Array.Empty<string>();selected=-1;}
        else if(fields.Length>=2&&int.TryParse(fields[^1],out selected))current=fields[..^1];
        // MPC-BE may omit the selected-index field when m_iSubtitleSel == -1,
        // even though the subtitle names remain. Accept only the known list.
        else if(savedIndex>=0&&fields.SequenceEqual(tracks)){current=fields;selected=-1;}
        else return;
        // A queued pre-command snapshot is not a manual selection. Keep the short
        // acknowledgement window; a later poll will still detect a user re-selection.
        if(AwaitingSuppression&&savedIndex>=0&&selected==savedIndex&&lastWall<waitUntil&&tracks.SequenceEqual(current))return;
        if (savedIndex >= 0 && !tracks.SequenceEqual(current)) { savedIndex = -1; userOverride = true; }
        tracks = current; active = selected;
        // Multiple verified commentary variants may exist. A name must identify exactly
        // one player track; ambiguous names must not suppress ordinary subtitles.
        var matches=(source?.Names??Array.Empty<string>()).Select(name=>Enumerable.Range(0,tracks.Length)
            .Where(index=>tracks[index].Contains(name,StringComparison.OrdinalIgnoreCase)).ToArray())
            .Where(indices=>indices.Length==1).Select(indices=>indices[0]);
        bool isComment=matches.Contains(active) || source?.OnlySubtitle==true&&tracks.Length==1&&active==0;
        AwaitingSuppression=false;waitUntil=double.NaN;CommentDisplayedByPlayer=isComment;
        if (savedIndex >= 0)
        {
            if (active >= 0) { savedIndex = -1; userOverride = true; }
            return;
        }
        if (!enabled || userOverride || source == null || active < 0 || active >= tracks.Length) return;
        if (isComment && send(MpcClient.SetSubtitleTrack, "-1")) { savedIndex = active; active = -1;AwaitingSuppression=true;waitUntil=lastWall+.5; }
    }
    public void Restore()
    {
        if (savedIndex >= 0 && active == -1)
            send(MpcClient.SetSubtitleTrack, savedIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
        savedIndex = -1;
    }
}
