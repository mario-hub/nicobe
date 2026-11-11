using System.Globalization;
using System.IO;

namespace NicoBE;

public static partial class ContainerComments
{
    public static PreparedComments ReadMkv(string path, Config config, CancellationToken token)
    {
        string ffmpeg = Encoder.EncoderSettings.Load().Ffmpeg;
        using var probe = MediaTools.Probe(ffmpeg, path, token);
        var metadata = Metadata(probe.RootElement.GetProperty("format"));
        var subtitles = probe.RootElement.GetProperty("streams").EnumerateArray()
            .Where(s => s.TryGetProperty("codec_type", out var type) && type.GetString() == "subtitle").ToArray();
        var tracks = subtitles.Where(s => s.TryGetProperty("codec_name", out var codec) && codec.GetString() is "ass" or "ssa")
            .OrderBy(s => AssCommentClassifier.TrackPriority(Tag(s,"title"))).ToArray();
        foreach (var track in tracks)
        {
            token.ThrowIfCancellationRequested();
            int index = track.GetProperty("index").GetInt32();
            string text = MediaTools.Capture(ffmpeg, new[] { "-v", "error", "-nostdin", "-copyts", "-i", path,
                "-map", "0:" + index.ToString(CultureInfo.InvariantCulture), "-c:s", "ass", "-f", "ass", "pipe:1" }, token);
            if(!AssCommentClassifier.IsComment(text))continue;
            var comments = AssComments.Read(text, token);
            if (comments.Count == 0) continue;
            string title = Tag(track, "title");
            var loaded = FromAss(path, comments, new(title.Length == 0 ? Array.Empty<string>() : new[] { title }, subtitles.Length == 1), metadata);
            return loaded;
        }
        if (External(path, token,config.MatchExternalAssPrefix) is { } external) return external;
        if (metadata != null) return LogReader.Prepare(metadata.Recording(path), config, token);
        throw new InvalidDataException("MKVに読み込めるASS字幕またはNicoBEの録画情報がありません。");
    }
}

