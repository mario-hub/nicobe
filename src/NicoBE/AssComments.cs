using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;

namespace NicoBE;

public static class AssComments
{
    static readonly CultureInfo Culture = CultureInfo.InvariantCulture;
    static readonly Regex Tags = new(@"\{([^}]*)\}", RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    static readonly Regex Alignment = new(@"\\an([1-9])", RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    static readonly Regex FontSize = new(@"\\fs([\d.]+)", RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    static readonly Regex PrimaryColor = new(@"\\(?:1c|c)&H([0-9a-fA-F]{1,8})&?", RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    // ASS sizes use the font cell (ascent + descent); DirectWrite uses the em.
    // Yu Gothic UI Bold: 2210 + 514 units per 2048-unit em.
    internal const double ExportFontScale = 2724d / 2048;
    const string ExportStyle = "NicoBE_Em_1.330078125";
    sealed record Style(double Size, string Color, int Alignment, double EmScale = 1);
    sealed record Placement(double Start, double End, double Width, double Height, double Y, bool Scroll)
    {
        public double X(double time) => Scroll ? 1920 - (time - Start) * Config.BaseScrollPixelsPerSecond : (1920 - Width) / 2;
    }

    public static List<Comment> Read(string text, CancellationToken token)
    {
        if (text.Length > 64 * 1024 * 1024) throw new InvalidDataException("ASSが大きすぎます。");
        string section = "";
        string[] format = Array.Empty<string>();
        var styles = new Dictionary<string, Style>(StringComparer.OrdinalIgnoreCase);
        var comments = new List<Comment>();
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
                styles[name] = new(Number(Field("Fontsize"), 38.4), Color(colorValue), styleAlignment, name == ExportStyle ? ExportFontScale : 1);
            }
            if (!section.Equals("[Events]", StringComparison.OrdinalIgnoreCase) || !key.Equals("Dialogue", StringComparison.OrdinalIgnoreCase)) continue;
            if (!Time(Field("Start"), out double start) || !Time(Field("End"), out double end) || end <= start) continue;
            string content = Field("Text");
            if (content.Length > 65536) continue;
            var style = styles.GetValueOrDefault(Field("Style")) ?? new Style(38.4, "#FFFFFF", 2);
            string tags = string.Join("", Tags.Matches(content).Select(m => m.Groups[1].Value));
            if (Regex.IsMatch(tags, @"\\p[1-9]", RegexOptions.None, TimeSpan.FromSeconds(1))) continue;
            int align = Alignment.Matches(tags).LastOrDefault() is { } alignment ? int.Parse(alignment.Groups[1].Value, Culture) : style.Alignment;
            double size = FontSize.Matches(tags).LastOrDefault() is { } fs ? Number(fs.Groups[1].Value, style.Size) : style.Size;
            string color = PrimaryColor.Matches(tags).LastOrDefault() is { } c ? Color(c.Groups[1].Value) : style.Color;
            var position = tags.Contains(@"\move(", StringComparison.OrdinalIgnoreCase) ? CommentPosition.Scroll
                : align >= 7 ? CommentPosition.Top : CommentPosition.Bottom;
            string plain = Tags.Replace(content, "").Replace(@"\N", "\n").Replace(@"\n", "\n").Replace(@"\h", " ").Trim();
            if (plain.Length is 0 or > 1000) continue;
            double scale = Math.Clamp(size / style.EmScale / resolutionY * 1080 / Config.BaseFontSize, .25, 3);
            if (seen.Add((start, plain, color, scale, position))) comments.Add(new(start, plain, color, scale, position));
            if (comments.Count > 500000) throw new InvalidDataException("ASSのコメント数が上限を超えました。");
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

    public static void Write(string path, IReadOnlyList<Comment> comments, CancellationToken token, double durationLimit = double.PositiveInfinity)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
        writer.WriteLine("[Script Info]\nTitle: NicoBE Comments\nScriptType: v4.00+\nPlayResX: 1920\nPlayResY: 1080\nWrapStyle: 2\nScaledBorderAndShadow: yes\n\n[V4+ Styles]");
        writer.WriteLine("Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding");
        writer.WriteLine($"Style: {ExportStyle},Yu Gothic UI,{(Config.BaseFontSize * ExportFontScale).ToString("0.###", Culture)},&H00FFFFFF,&H00FFFFFF,&H00000000,&H00000000,-1,0,0,0,100,100,0,0,1,2,0,7,0,0,0,1\n\n[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text");
        var typeface = new Typeface(new FontFamily("Yu Gothic UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        double laneHeight = Config.BaseFontSize * (1 + .65 * Config.DefaultLineSpacingPercent / 100);
        var active = new List<Placement>();
        foreach (var comment in comments)
        {
            token.ThrowIfCancellationRequested();
            double size = Config.BaseFontSize * comment.SizeScale;
            string escaped = comment.Text.Replace('{', '｛').Replace('}', '｝').Replace('\\', '＼').Replace("\r\n", "\n").Replace('\r', '\n');
            var text = new FormattedText(escaped, CultureInfo.GetCultureInfo("ja-JP"), FlowDirection.LeftToRight, typeface, size, Brushes.White, 1);
            double width = text.WidthIncludingTrailingWhitespace;
            double height = Math.Max(text.Height,size * ExportFontScale);
            double duration = comment.Position == CommentPosition.Scroll ? (1920 + width) / Config.BaseScrollPixelsPerSecond : 5;
            duration = Math.Min(duration, durationLimit - comment.MediaSeconds);
            if (duration < .01) continue;
            active.RemoveAll(a => a.End <= comment.MediaSeconds);
            int lanes = Math.Max(1, (int)((1080 - 10 - height) / laneHeight) + 1);
            Placement? chosen = null;
            int fewest = int.MaxValue;
            for (int lane = 0; lane < lanes; lane++)
            {
                double top = comment.Position == CommentPosition.Bottom ? 1080 - 5 - height - lane * laneHeight : 5 + lane * laneHeight;
                var candidate = new Placement(comment.MediaSeconds, comment.MediaSeconds + duration, width, height, top, comment.Position == CommentPosition.Scroll);
                int overlaps = active.Count(a => Intersects(a, candidate));
                if (overlaps < fewest) { chosen = candidate; fewest = overlaps; }
                if (overlaps == 0) break;
            }
            var placed = chosen!;
            active.Add(placed);
            string y = placed.Y.ToString("0.###", Culture);
            string movement = comment.Position == CommentPosition.Scroll
                ? $"\\an7\\move(1920,{y},{placed.X(placed.End).ToString("0.###", Culture)},{y})"
                : comment.Position == CommentPosition.Top ? $"\\an8\\pos(960,{y})" : $"\\an2\\pos(960,{(placed.Y + height).ToString("0.###", Culture)})";
            escaped=escaped.Replace("\n",@"\N");
            string rgb = comment.Color.TrimStart('#');
            string bgr = rgb[4..6] + rgb[2..4] + rgb[..2];
            writer.WriteLine($"Dialogue: 0,{Stamp(comment.MediaSeconds)},{Stamp(comment.MediaSeconds + duration)},{ExportStyle},,0,0,0,,{{{movement}\\fs{(size * ExportFontScale).ToString("0.###", Culture)}\\c&H{bgr}&}}{escaped}");
        }
    }

    static bool Intersects(Placement first, Placement second)
    {
        if (first.Y + first.Height + 2 <= second.Y || second.Y + second.Height + 2 <= first.Y) return false;
        // Linear motion: if the same rectangle stays to one side at both ends,
        // it cannot collide during their shared lifetime. This allows row reuse.
        double end = Math.Min(first.End, second.End);
        bool Left(Placement a, Placement b, double time) => a.X(time) + a.Width + 35 <= b.X(time);
        return !(Left(first, second, second.Start) && Left(first, second, end)
            || Left(second, first, second.Start) && Left(second, first, end));
    }
}
