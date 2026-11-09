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

    public SubtitleControl(Func<uint, string, bool> send) => this.send = send;
    public void Reset()
    {
        source = null; tracks = Array.Empty<string>(); savedIndex = -1; active = -1;
        userOverride = false; lastQuery = double.NegativeInfinity;
    }
    public void SetSource(AssSubtitle? value) { source = value; lastQuery = double.NegativeInfinity; }
    public void Poll(bool visible, double wall)
    {
        enabled = visible;
        if (source == null || wall - lastQuery < 1) return;
        lastQuery = wall;
        send(MpcClient.GetSubtitleTracks, "");
    }
    public void Receive(string value)
    {
        var fields = MpcClient.SplitFields(value);
        if (fields.Length < 2 || !int.TryParse(fields[^1], out int selected)) return;
        var current = fields[..^1];
        if (savedIndex >= 0 && !tracks.SequenceEqual(current)) { savedIndex = -1; userOverride = true; }
        tracks = current; active = selected;
        if (savedIndex >= 0)
        {
            if (active >= 0) { savedIndex = -1; userOverride = true; }
            else if (!enabled) Restore();
            return;
        }
        if (!enabled || userOverride || source == null || active < 0 || active >= tracks.Length) return;
        var matches = Enumerable.Range(0, tracks.Length).Where(index =>
            source.Names.Any(name => tracks[index].Contains(name, StringComparison.OrdinalIgnoreCase))).ToArray();
        bool isComment = matches.Length == 1 && matches[0] == active || source.OnlySubtitle && tracks.Length == 1;
        if (isComment && send(MpcClient.SetSubtitleTrack, "-1")) { savedIndex = active; active = -1; }
    }
    public void Restore()
    {
        if (savedIndex >= 0 && active == -1)
            send(MpcClient.SetSubtitleTrack, savedIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
        savedIndex = -1;
    }
}
