using System.IO;
using System.Text.Json;

namespace NicoBE;

public sealed record AssSubtitle(string[] Names, bool OnlySubtitle);

public static partial class ContainerComments
{
    public const string TrackTitle = "NicoBE Comments";
    public const string MetadataTag = "NICOBE_RECORDING";

    static string Tag(JsonElement element, string name)
    {
        if (element.TryGetProperty("tags", out var tags))
            foreach (var tag in tags.EnumerateObject())
                if (tag.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return tag.Value.GetString() ?? "";
        return "";
    }

    public static Mp4RecordingMetadata? Metadata(JsonElement format) => ParseMetadata(Tag(format, MetadataTag));

    public static Mp4RecordingMetadata? RecordingMetadata(string value)=>ParseMetadata(value);
    static Mp4RecordingMetadata? ParseMetadata(string value)
    {
        if (value.Length == 0) return null;
        if (value.Length > 65536) throw new InvalidDataException("録画情報が大きすぎます。");
        var metadata = JsonSerializer.Deserialize<Mp4RecordingMetadata>(value) ?? throw new InvalidDataException("録画情報を読めません。");
        metadata.Validate();
        return metadata;
    }

    public static string[] ExternalFiles(string video, bool matchPrefix = true)
    {
        string stem = Path.GetFileNameWithoutExtension(video);
        string directory = Path.GetDirectoryName(video)!;
        // Exact basename first; optional prefix matches are fallback candidates only.
        return Directory.EnumerateFiles(directory, "*.ass")
            .Where(path => Path.GetFileNameWithoutExtension(path).Equals(stem, StringComparison.OrdinalIgnoreCase)
                || matchPrefix && Path.GetFileNameWithoutExtension(path).StartsWith(stem, StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => Path.GetFileNameWithoutExtension(path).Equals(stem, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(path => Path.GetFileName(path).StartsWith(stem + ".",StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(path).StartsWith(stem + "-nicojk",StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase).Take(32).ToArray();
    }

    static PreparedComments FromAss(string video, List<Comment> comments, AssSubtitle subtitle, Mp4RecordingMetadata? metadata = null)
    {
        double duration = comments.Count == 0 ? 1 : comments[^1].MediaSeconds + 30;
        var recording = metadata?.Recording(video) ?? new RecordingInfo(video, 0, 0, 0,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddSeconds(duration), 0);
        return new(recording, "ASS", comments, 1, 0) { Subtitle = subtitle };
    }

    public static PreparedComments? External(string video, CancellationToken token, bool matchPrefix = true)
    {
        foreach (string path in ExternalFiles(video,matchPrefix))
        {
            if(new FileInfo(path).Length>64*1024*1024)continue;
            string document=File.ReadAllText(path);
            if(!AssCommentClassifier.IsComment(document))continue;
            var comments = AssComments.Read(document, token);
            if (comments.Count == 0) continue;
            var loaded = FromAss(video, comments, new(new[] { Path.GetFileName(path), Path.GetFileNameWithoutExtension(path) }, false));
            return loaded;
        }
        return null;
    }
}
