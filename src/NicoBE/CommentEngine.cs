namespace NicoBE;

public sealed class ActiveComment
{
    public required Comment Comment { get; init; }
    public required double Born { get; init; }
    public required int Lane { get; init; }
    public required double Width { get; init; }
    public int LaneSpan { get; init; }=1;
    public object? Visual { get; init; }
}

public sealed class CommentEngine
{
    public const double Width=1920, Height=1080;
    private readonly Config config;
    private NgFilter ng;
    private List<Comment> comments=new();
    private int cursor;
    private double? lastTick;
    private double lastResponse=double.NegativeInfinity;
    private bool anchored;
    public List<ActiveComment> Active { get; }=new();
    public bool Playing { get; private set; }
    public bool Ready { get; private set; }
    public double MediaPosition { get; private set; }
    public double MotionSeconds { get; private set; }
    public long Dropped { get; private set; }
    public int SeekGeneration { get; private set; }
    // Adjust the extra space between rows; retain the text's own height.
    public double LaneHeight => config.FontSize*(1+.65*config.LineSpacingPercent/100);
    public Func<Comment,(double Width,double Height,object? Visual)> Measure { get; set; }=c=>(c.Text.Split('\n').Max(l=>l.Length)*28d,(1+c.Text.Count(ch=>ch=='\n'))*Config.BaseFontSize*1.4,null);
    public CommentEngine(Config config) {this.config=config;ng=new(config);}
    public void Reset()
    {
        comments=new();cursor=0;Ready=false;anchored=false;Active.Clear();MediaPosition=0;
        lastResponse=double.NegativeInfinity;MotionSeconds=0;lastTick=null;Playing=false;Dropped=0;SeekGeneration++;
    }
    public void SetComments(IReadOnlyList<Comment> value)
    {
        comments=value.ToList();Ready=true;cursor=LowerBound(MediaPosition);Active.Clear();
        // Loading can finish after playback has already advanced. Reconstruct
        // the same recent history as a seek, regardless of notification order.
        if(anchored)RestoreHistory();
    }
    public int CommentCount=>comments.Count;
    public void AppendComments(IReadOnlyList<Comment> added)
    {
        if(!Ready || comments.Count==0){SetComments(added);return;}
        if(added.Count==0)return;
        if(comments.Count==0 || added[0].MediaSeconds>=comments[^1].MediaSeconds)
        {comments.AddRange(added);return;}
        // Usually append-only. Late/out-of-order comments require merging only the affected suffix.
        int start=LowerBound(added[0].MediaSeconds+config.CommentOffsetSeconds);
        int i=start,j=0,oldCursor=cursor,beforeCursor=0,budget=150;
        var suffix=new List<Comment>(comments.Count-start+added.Count);
        while(i<comments.Count || j<added.Count)
        {
            if(j==added.Count || i<comments.Count && comments[i].MediaSeconds<=added[j].MediaSeconds)
                suffix.Add(comments[i++]);
            else
            {
                var c=added[j++];suffix.Add(c);
                if(i<oldCursor)
                {
                    beforeCursor++;
                    if(Playing && Due(c)>=MediaPosition-1.2 && Due(c)<=MediaPosition && budget-->0)Add(c,MotionSeconds);
                }
            }
        }
        comments.RemoveRange(start,comments.Count-start);comments.AddRange(suffix);cursor=oldCursor+beforeCursor;
    }
    public void SetPlaying(bool value,double wall) {Tick(wall);Playing=value;}
    public void Tick(double wall)
    {
        if(lastTick is {} previous && Playing)
        {
            // The GPU interpolates between snapshots. Keep the same real-time clock
            // after a delayed UI callback, instead of snapping it backwards by a frame cap.
            // Freeze when the API liveness lease expires.
            double until=Math.Min(wall,lastResponse+.65);
            MotionSeconds+=Math.Max(0,until-previous);
        }
        lastTick=wall;
        Active.RemoveAll(a=>a.Comment.Position==CommentPosition.Scroll ? X(a)+a.Width<0 : MotionSeconds-a.Born>=config.FixedCommentSeconds);
    }
    public double X(ActiveComment a) => a.Comment.Position==CommentPosition.Scroll
        ? Width-(MotionSeconds-a.Born)*config.ScrollPixelsPerSecond : (Width-a.Width)/2;
    public double Y(ActiveComment a) => a.Lane*LaneHeight+5;
    public void OnSeek(double media,double wall)
    {
        Tick(wall);MediaPosition=media;lastResponse=wall;Active.Clear();anchored=true;SeekGeneration++;
        if(Ready)RestoreHistory();else cursor=LowerBound(media);
    }
    public void RefreshSettings(){ng=new(config);RestoreHistory();}
    public IReadOnlyList<NgLogRow> NearbyComments(double position)
    {
        var rows=new List<NgLogRow>();
        for(int i=LowerBound(position-5);i<comments.Count && Due(comments[i])<=position+5;i++)
        {
            var c=comments[i];rows.Add(new(Due(c),c.Text,c.UserId));
        }
        return rows;
    }
    private double Due(Comment c)=>c.MediaSeconds+config.CommentOffsetSeconds;
    private void RestoreHistory()
    {
        Active.Clear();cursor=LowerBound(MediaPosition-10);
        while(cursor<comments.Count && Due(comments[cursor])<=MediaPosition)
        {
            var c=comments[cursor++];Add(c,MotionSeconds-(MediaPosition-Due(c)));
        }
    }
    public void OnPosition(double media,double wall)
    {
        Tick(wall);
        double elapsed=wall-lastResponse,delta=media-MediaPosition;
        if(!anchored)OnSeek(media,wall);
        else if(delta<-.2 || (elapsed<1 && delta>Math.Max(2,elapsed*12))) OnSeek(media,wall);
        MediaPosition=media;lastResponse=wall;
        if(!Ready || !Playing) return;
        int stale=LowerBound(media-1.2);
        if(stale>cursor){Dropped+=stale-cursor;cursor=stale;}
        int end=LowerBound(media,true),limit=Math.Min(end,cursor+150);
        while(cursor<limit)
        {
            var c=comments[cursor++];
            Add(c,MotionSeconds);
        }
        if(cursor<end){Dropped+=end-cursor;cursor=end;}
    }
    private void Add(Comment comment,double born)
    {
        if(ng.Blocks(comment))return;
        double age=MotionSeconds-born;
        if(comment.Position!=CommentPosition.Scroll && age>=config.FixedCommentSeconds)return;
        if(Active.Count>=config.MaxVisibleComments) {Dropped++;return;}
        var (width,height,visual)=Measure(comment);
        // Keep the user's existing single-line spacing, even when font metrics
        // slightly exceed a lane. Only explicit line breaks occupy extra lanes.
        bool multiline=comment.Text.Contains('\n') || comment.Text.Contains('\r');
        int span=multiline?Math.Max(1,(int)Math.Ceiling(height/LaneHeight)):1;
        double x=comment.Position==CommentPosition.Scroll?Width-age*config.ScrollPixelsPerSecond:(Width-width)/2;
        if(comment.Position==CommentPosition.Scroll && x+width<0)return;
        int lanes=Math.Max(1,(int)(Height/LaneHeight))-span+1;
        if(lanes<1){Dropped++;return;}
        IEnumerable<int> candidates=comment.Position==CommentPosition.Bottom ? Enumerable.Range(0,lanes).Reverse() : Enumerable.Range(0,lanes);
        foreach(int lane in candidates)
        {
            bool busy=Active.Any(a=>a.Lane<lane+span && lane<a.Lane+a.LaneSpan && (comment.Position!=CommentPosition.Scroll || a.Comment.Position!=CommentPosition.Scroll || X(a)+a.Width>x-35));
            if(busy) continue;
            Active.Add(new() {Comment=comment,Born=born,Lane=lane,LaneSpan=span,Width=width,Visual=visual});return;
        }
        Dropped++;
    }
    private int LowerBound(double position,bool afterEqual=false)
    {
        int lo=0,hi=comments.Count;
        while(lo<hi){int mid=(lo+hi)/2;double time=Due(comments[mid]);if(time<position || afterEqual && time==position)lo=mid+1;else hi=mid;}
        return lo;
    }
}
