namespace NicoBE;

public sealed class NgFilter
{
    readonly HashSet<string> ids;
    readonly string[] words;
    readonly bool colored,fixedPosition,sizeChanged,multiline;
    public NgFilter(Config config)
    {
        ids=new(Normalize(config.NgIds,StringComparer.Ordinal),StringComparer.Ordinal);
        words=Normalize(config.NgWords,StringComparer.OrdinalIgnoreCase).ToArray();
        colored=config.NgColoredComments;fixedPosition=config.NgFixedComments;
        sizeChanged=config.NgSizeChangedComments;multiline=config.NgMultilineComments;
    }
    public bool Blocks(Comment comment)=>
        (colored && !string.Equals(comment.Color,"#FFFFFF",StringComparison.OrdinalIgnoreCase)) ||
        (fixedPosition && comment.Position!=CommentPosition.Scroll) ||
        (sizeChanged && Math.Abs(comment.SizeScale-1)>0.001) ||
        (multiline && (comment.Text.Contains('\n') || comment.Text.Contains('\r'))) ||
        (comment.UserId.Length>0 && ids.Contains(comment.UserId)) ||
        words.Any(word=>comment.Text.Contains(word,StringComparison.OrdinalIgnoreCase));
    public static List<string> Normalize(IEnumerable<string>? entries,StringComparer comparer)=>
        (entries??Array.Empty<string>()).Where(s=>!string.IsNullOrWhiteSpace(s)).Select(s=>s.Trim()).Distinct(comparer).ToList();
}

public sealed record NgLogRow(double Seconds,string Text,string UserId)
{
    public string Time
    {
        get
        {
            var t=TimeSpan.FromSeconds(Math.Abs(Seconds));
            return $"{(Seconds<0?"−":"")}{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds:000}";
        }
    }
}
public sealed record NgLogSnapshot(string Title,double Position,bool Ready,IReadOnlyList<NgLogRow> Rows);
