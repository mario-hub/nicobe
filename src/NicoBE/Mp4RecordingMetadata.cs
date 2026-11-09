using System.Text.Json.Serialization;
using System.IO;
using System.Text.Json;

namespace NicoBE;

// A private ISO BMFF uuid box travels with the MP4; no source TS or sidecar is needed.
public sealed record Mp4RecordingMetadata([property:JsonRequired] int Version,[property:JsonRequired] DateTimeOffset PlaybackZeroUtc,
    [property:JsonRequired] DateTimeOffset SourceStartUtc,[property:JsonRequired] int ServiceId,[property:JsonRequired] int NetworkId,[property:JsonRequired] string SourceFileName,
    [property:JsonRequired] double DurationSeconds,[property:JsonRequired] double SourceFirstVideoPts,[property:JsonRequired] double OutputFirstVideoPts)
{
    static readonly byte[] Id=Convert.FromHexString("B728B028BAB6DE44979A30EB28E71C5D");
    public void Validate()
    {
        if(Version!=1 || SourceFileName==null || ServiceId<=0 || NetworkId<=0 || !double.IsFinite(DurationSeconds) || DurationSeconds<=0
            || !double.IsFinite(SourceFirstVideoPts) || !double.IsFinite(OutputFirstVideoPts)
            || PlaybackZeroUtc.Year<1990 || PlaybackZeroUtc.Year>2200
            || DurationSeconds>(DateTimeOffset.MaxValue-PlaybackZeroUtc).TotalSeconds)
            throw new InvalidDataException("NicoBEの録画メタデータが不正または未対応です。");
    }
    public void Append(string path)
    {
        Validate();byte[] json=JsonSerializer.SerializeToUtf8Bytes(this);
        if(json.Length>65536)throw new InvalidDataException("録画メタデータが大きすぎます。");
        Mp4UuidBox.Append(path,Id,json,65536,CancellationToken.None);
    }
    public static Mp4RecordingMetadata Read(string path,CancellationToken token)
    {
        var json=Mp4UuidBox.Read(path,Id,65536,token)??throw new InvalidDataException("このMP4にはNicoBEの録画メタデータがありません。");
        var data=Mp4UuidBox.Deserialize<Mp4RecordingMetadata>(json);data.Validate();return data;
    }
    public RecordingInfo Recording(string path)=>new(path,ServiceId,NetworkId,0,PlaybackZeroUtc,PlaybackZeroUtc.AddSeconds(DurationSeconds),0);
}
