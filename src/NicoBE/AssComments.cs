using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;

namespace NicoBE;

public static class AssComments
{
    // Explicit size reference for older NicoBE exports only.
    internal const double ReferenceFontSize = 38.4;
    static readonly CultureInfo Culture = CultureInfo.InvariantCulture;
    static readonly Regex Tags = new(@"\{([^}]*)\}", RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    static readonly Regex Alignment = new(@"\\an([1-9])", RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    static readonly Regex FontSize = new(@"\\fs([\d.]+)", RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    static readonly Regex ScaleY = new(@"\\fscy([\d.]+)", RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    static readonly Regex PrimaryColor = new(@"\\(?:1c|c)&H([0-9a-fA-F]{1,8})&?", RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    // ASS sizes use the font cell (ascent + descent); DirectWrite uses the em.
    // Yu Gothic UI Bold: 2210 + 514 units per 2048-unit em.
    internal const double ExportFontScale = 2724d / 2048;
    const string ExportStyle = "NicoBE_Em_1.330078125";
    static readonly Regex ExportMetrics = new(@"^NicoBE_Em_([0-9.]+)_Base([0-9.]+)$", RegexOptions.CultureInvariant);
    sealed record Style(double Size, string Color, int Alignment, double EmScale = 1, double ReferenceSize = 0, double ScaleY = 100);
    sealed record Placement(double Start, double End, double Width, double Height, double Y, double Speed, int Lane, int Span)
    {
        public double X(double time) => Speed>0 ? 1920 - (time - Start) * Speed : (1920 - Width) / 2;
    }

    public static List<Comment> Read(string text, CancellationToken token)
    {
        if (text.Length > 64 * 1024 * 1024) throw new InvalidDataException("ASSが大きすぎます。");
        string section = "";
        string[] format = Array.Empty<string>();
        var styles = new Dictionary<string, Style>(StringComparer.OrdinalIgnoreCase);
        var comments = new List<Comment>();
        var externalIndices = new List<int>();
        var standardSizes = new HashSet<double>();
        bool missingStandard = false;
        var seen = new HashSet<(double, string, string, double, CommentPosition)>();
        double resolutionY = 1080;
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
        {
            token.ThrowIfCancellationRequested();
            line = line.Trim();
            if (line.StartsWith('[')) { section = line; format = Array.Empty<string>(); continue; }
            int colon = line.IndexOf(':');
            if (colon < 0) continue;
            string key = line[..colon], value = line[(colon + 1)..].TrimStart();
            if (key.Equals("PlayResY", StringComparison.OrdinalIgnoreCase)) resolutionY = Math.Clamp(Number(value, 1080), 1, 16384);
            if (key.Equals("Format", StringComparison.OrdinalIgnoreCase)) { format = value.Split(',').Select(s => s.Trim()).ToArray(); continue; }
            if (format.Length == 0) continue;
            var fields = value.Split(',', format.Length);
            if (fields.Length != format.Length) continue;
            string Field(string name, string fallback = "")
            {
                int index = Array.FindIndex(format, f => f.Equals(name, StringComparison.OrdinalIgnoreCase));
                return index < 0 ? fallback : fields[index].Trim();
            }
            bool ssa = section.Equals("[V4 Styles]", StringComparison.OrdinalIgnoreCase);
            if ((ssa || section.Equals("[V4+ Styles]", StringComparison.OrdinalIgnoreCase)) && key.Equals("Style", StringComparison.OrdinalIgnoreCase))
            {
                string name = Field("Name");
                int styleAlignment = (int)Number(Field("Alignment"), 2);
                if (ssa) styleAlignment = styleAlignment is >= 5 and <= 7 ? 7 : 2;
                string colorValue = Field("PrimaryColour");
                if (ssa && int.TryParse(colorValue, NumberStyles.Integer, Culture, out int decimalColor))
                    colorValue = unchecked((uint)decimalColor).ToString("X8", Culture);
                var metrics=ExportMetrics.Match(name);
                double emScale=name==ExportStyle?ExportFontScale:1,reference=name==ExportStyle?ReferenceFontSize:0;
                if(metrics.Success)
                {
                    emScale=Math.Clamp(Number(metrics.Groups[1].Value,1),.5,4);
                    reference=Math.Clamp(Number(metrics.Groups[2].Value,ReferenceFontSize),16,144);
                }
                styles[name] = new(Number(Field("Fontsize"), 0), Color(colorValue), styleAlignment, emScale,reference,Math.Clamp(Number(Field("ScaleY"),100),1,1000));
            }
            if (!section.Equals("[Events]", StringComparison.OrdinalIgnoreCase) || !key.Equals("Dialogue", StringComparison.OrdinalIgnoreCase)) continue;
            if (!Time(Field("Start"), out double start) || !Time(Field("End"), out double end) || end <= start) continue;
            string content = Field("Text");
            if (content.Length > 65536 || AssCommentClassifier.IsManagementEvent(content)) continue;
            var style = styles.GetValueOrDefault(Field("Style")) ?? new Style(0, "#FFFFFF", 2);
            string tags = string.Join("", Tags.Matches(content).Select(m => m.Groups[1].Value));
            if (Regex.IsMatch(tags, @"\\p[1-9]", RegexOptions.None, TimeSpan.FromSeconds(1))) continue;
            int align = Alignment.Matches(tags).LastOrDefault() is { } alignment ? int.Parse(alignment.Groups[1].Value, Culture) : style.Alignment;
            double defaultSize = style.Size > 0 ? style.Size : ReferenceFontSize;
            double size = FontSize.Matches(tags).LastOrDefault() is { } fs ? Number(fs.Groups[1].Value, defaultSize) : defaultSize;
            if(size<=0)size=defaultSize;
            string color = PrimaryColor.Matches(tags).LastOrDefault() is { } c ? Color(c.Groups[1].Value) : style.Color;
            var position = tags.Contains(@"\move(", StringComparison.OrdinalIgnoreCase) ? CommentPosition.Scroll
                : align >= 7 ? CommentPosition.Top : CommentPosition.Bottom;
            if(AssCommentClassifier.TryPlacement(content,out bool moving,out double y,out bool isMove)&&isMove)
                position=moving?CommentPosition.Scroll:y<resolutionY/2?CommentPosition.Top:CommentPosition.Bottom;
            string plain = Tags.Replace(content, "").Replace(@"\N", "\n").Replace(@"\n", "\n").Replace(@"\h", " ").Trim();
            if (plain.Length is 0 or > 1000) continue;
            double scaleY=ScaleY.Matches(tags).LastOrDefault() is {} sy?Math.Clamp(Number(sy.Groups[1].Value,style.ScaleY),1,1000):style.ScaleY;
            double scale = size * scaleY / 100 / style.EmScale / resolutionY * 1080;
            bool explicitReference = style.ReferenceSize > 0;
            if(explicitReference)scale=Math.Clamp(scale/style.ReferenceSize,.25,3);
            if (seen.Add((start, plain, color, scale, position)))
            {
                if(!explicitReference)
                {
                    externalIndices.Add(comments.Count);
                    if(style.Size>0)standardSizes.Add(Math.Round(style.Size*style.ScaleY/100/resolutionY*1080,4));
                    else missingStandard=true;
                }
                comments.Add(new(start, plain, color, scale, position));
            }
            if (comments.Count > 500000) throw new InvalidDataException("ASSのコメント数が上限を超えました。");
        }
        if(externalIndices.Count>0)
        {
            // Shared style size is the normal size, even if the first comments are big/small.
            // Otherwise estimate from the first 100 usable, deduplicated comments in time order.
            double standard=standardSizes.Count==1&&!missingStandard?standardSizes.Single():0;
            if(standard<=0)
            {
                var sample=externalIndices.OrderBy(i=>comments[i].MediaSeconds).Take(100)
                    .Select(i=>Math.Round(comments[i].SizeScale,4)).OrderBy(s=>s).ToArray();
                double median=(sample[(sample.Length-1)/2]+sample[sample.Length/2])/2;
                // On a frequency tie choose the size nearest the median, then the smaller size.
                standard=sample.GroupBy(s=>s).OrderByDescending(g=>g.Count())
                    .ThenBy(g=>Math.Abs(g.Key-median)).ThenBy(g=>g.Key).First().Key;
            }
            if(standard<=0)standard=ReferenceFontSize;
            foreach(int i in externalIndices)
            {
                token.ThrowIfCancellationRequested();
                comments[i]=comments[i] with { SizeScale=Math.Clamp(comments[i].SizeScale/standard,.25,3) };
            }
        }
        return comments.OrderBy(c => c.MediaSeconds).ToList();
    }

    public static List<Comment> ReadFile(string path, CancellationToken token)
    {
        if (new FileInfo(path).Length > 64 * 1024 * 1024) throw new InvalidDataException("ASSが大きすぎます。");
        return Read(File.ReadAllText(path, Encoding.UTF8), token);
    }
    static double Number(string value, double fallback) => double.TryParse(value, NumberStyles.Float, Culture, out double result) && double.IsFinite(result) ? result : fallback;
    static bool Time(string value, out double seconds)
    {
        seconds = 0;
        string[] parts = value.Split(':');
        if (parts.Length != 3) return false;
        double hours = Number(parts[0], -1), minutes = Number(parts[1], -1), rest = Number(parts[2], -1);
        if (hours < 0 || hours > 10000 || minutes < 0 || minutes >= 60 || rest < 0 || rest >= 60) return false;
        seconds = hours * 3600 + minutes * 60 + rest;
        return true;
    }
    static string Color(string value)
    {
        value = value.Trim().Replace("&H", "", StringComparison.OrdinalIgnoreCase).TrimEnd('&');
        if (!uint.TryParse(value, NumberStyles.HexNumber, Culture, out uint bgr)) return "#FFFFFF";
        return $"#{bgr & 255:X2}{(bgr >> 8) & 255:X2}{(bgr >> 16) & 255:X2}";
    }
    static string Stamp(double seconds)
    {
        long centiseconds = (long)Math.Round(Math.Max(0, seconds) * 100);
        return $"{centiseconds / 360000}:{centiseconds / 6000 % 60:00}:{centiseconds / 100 % 60:00}.{centiseconds % 100:00}";
    }

    public static int Write(string path, IReadOnlyList<Comment> comments, CancellationToken token, double durationLimit = double.PositiveInfinity, double fontSizePercent = CommentDisplayDefaults.FontSizePercent)
        => Write(path,comments,token,durationLimit,new AssStyleSettings {FontSizePercent=fontSizePercent});

    public static int Write(string path, IReadOnlyList<Comment> comments, CancellationToken token, double durationLimit, AssStyleSettings style)
    {
        style.Validate();
        double baseSize=CommentDisplayDefaults.FontSize*style.FontSizePercent/100;
        var typeface = new Typeface(new FontFamily(style.FontFamily), FontStyles.Normal, FontWeight.FromOpenTypeWeight(style.FontWeight), FontStretches.Normal);
        double fontScale=typeface.TryGetGlyphTypeface(out var glyph)&&double.IsFinite(glyph.Height)&&glyph.Height>0?glyph.Height:ExportFontScale;
        string styleName="NicoBE_Em_"+fontScale.ToString("0.#########",Culture)+"_Base"+CommentDisplayDefaults.FontSize.ToString("0.###",Culture);
        string alpha=((int)Math.Round((1-style.OpacityPercent/100)*255)).ToString("X2",Culture);
        string shadowAlpha=((int)Math.Round((1-style.OpacityPercent/100*CommentDisplayDefaults.ShadowOpacityScale)*255)).ToString("X2",Culture);
        using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
        writer.WriteLine("[Script Info]\nTitle: NicoBE Comments\nScriptType: v4.00+\nPlayResX: 1920\nPlayResY: 1080\nWrapStyle: 2\nScaledBorderAndShadow: yes\n\n[V4+ Styles]");
        writer.WriteLine("Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding");
        writer.WriteLine($"Style: {styleName},{style.FontFamily},{(baseSize * fontScale).ToString("0.###", Culture)},&H{alpha}FFFFFF,&H{alpha}FFFFFF,&H{alpha}000000,&H{shadowAlpha}000000,{(style.FontWeight>=700?-1:0)},0,0,0,100,100,0,0,1,{CommentDisplayDefaults.OutlineWidth(style.OutlineLevel).ToString("0.###",Culture)},{CommentDisplayDefaults.ShadowOffset(style.ShadowLevel).ToString("0.###",Culture)},7,0,0,0,1\n\n[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text");
        double laneHeight = baseSize * (1 + style.LineSpacingPercent / 100);
        var active = new List<Placement>();
        int written=0;
        foreach (var comment in comments)
        {
            token.ThrowIfCancellationRequested();
            double size = baseSize * comment.SizeScale;
            string escaped = comment.Text.Replace('{', '｛').Replace('}', '｝').Replace('\\', '＼').Replace("\r\n", "\n").Replace('\r', '\n');
            var text = new FormattedText(escaped, CultureInfo.GetCultureInfo("ja-JP"), FlowDirection.LeftToRight, typeface, size, Brushes.White, 1);
            double width = text.WidthIncludingTrailingWhitespace;
            double height = Math.Max(text.Height,size * fontScale);
            double speed=comment.Position==CommentPosition.Scroll?CommentDisplayDefaults.ScrollPixelsPerSecond*style.ScrollSpeedPercent/100*(style.AccelerateLongComments?CommentMotion.LengthSpeedMultiplier(comment.Text):1):0;
            double start=comment.MediaSeconds-style.AdvanceSeconds,visibleStart=Math.Max(0,start);
            double end=Math.Min(start+(speed>0 ? (1920 + width) / speed : style.FixedCommentSeconds),durationLimit);
            if (end-visibleStart < .01) continue;
            active.RemoveAll(a => a.End <= visibleStart);
            if(active.Count>=style.MaxVisibleComments)continue;
            int span=escaped.Contains('\n')?Math.Max(1,(int)Math.Ceiling(height/laneHeight)):1;
            int lanes=Math.Max(1,(int)(1080/laneHeight))-span+1;
            Placement? chosen = null;
            for (int index = 0; index < lanes; index++)
            {
                int lane=comment.Position==CommentPosition.Bottom?lanes-1-index:index;
                double top=5+lane*laneHeight;
                var candidate = new Placement(start,end, width, height, top, speed,lane,span);
                if(!active.Any(a=>Intersects(a,candidate))){chosen=candidate;break;}
            }
            if(chosen==null)continue;
            var placed = chosen;
            active.Add(placed);
            string y = placed.Y.ToString("0.###", Culture);
            string movement = comment.Position == CommentPosition.Scroll
                ? $"\\an7\\move({placed.X(visibleStart).ToString("0.###",Culture)},{y},{placed.X(placed.End).ToString("0.###", Culture)},{y})"
                : comment.Position == CommentPosition.Top ? $"\\an8\\pos(960,{y})" : $"\\an2\\pos(960,{(placed.Y + height).ToString("0.###", Culture)})";
            escaped=escaped.Replace("\n",@"\N");
            string rgb = comment.Color.TrimStart('#');
            string bgr = rgb[4..6] + rgb[2..4] + rgb[..2];
            writer.WriteLine($"Dialogue: 0,{Stamp(visibleStart)},{Stamp(end)},{styleName},,0,0,0,,{{{movement}\\b{style.FontWeight}\\fs{(size * fontScale).ToString("0.###", Culture)}\\c&H{bgr}&}}{escaped}");
            written++;
        }
        return written;
    }

    static bool Intersects(Placement first, Placement second)
    {
        if (first.Lane+first.Span<=second.Lane || second.Lane+second.Span<=first.Lane) return false;
        if(first.Speed==0 || second.Speed==0)return true;
        // Linear motion: if the same rectangle stays to one side at both ends,
        // it cannot collide during their shared lifetime. This allows row reuse.
        double end = Math.Min(first.End, second.End);
        double start = Math.Max(0,second.Start);
        bool Left(Placement a, Placement b, double time) => a.X(time) + a.Width + 35 <= b.X(time);
        return !(Left(first, second, start) && Left(first, second, end)
            || Left(second, first, start) && Left(second, first, end));
    }
}
