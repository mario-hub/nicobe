using System.ComponentModel;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;

namespace NicoBE;

public static partial class ContainerComments
{
    public static PreparedComments ReadMkv(string path, Config config, CancellationToken token)
    {
        var (recordingTag, subtitleCount, tracks) = MkvReader.Read(path, token);
        var metadata = ParseMetadata(recordingTag);
        foreach (var track in tracks.OrderBy(t => AssCommentClassifier.TrackPriority(t.Title)))
        {
            token.ThrowIfCancellationRequested();
            if (track.Document is null || !AssCommentClassifier.IsComment(track.Document.ToString())) continue;
            var comments = AssComments.Read(track.Document.ToString(), token);
            if (comments.Count == 0) continue;
            var names = track.Title.Length == 0 ? Array.Empty<string>() : new[] { track.Title };
            var loaded = FromAss(path, comments, new(names, subtitleCount == 1), metadata);
            return loaded;
        }
        if (External(path, token,config.MatchExternalAssPrefix) is { } external) return external;
        if (metadata != null) return LogReader.Prepare(metadata.Recording(path), config, token);
        throw new InvalidDataException("MKVに読み込めるASS字幕またはNicoBEの録画情報がありません。");
    }
}

internal static class MkvReader
{
    const int MaxAssBytes = 64 * 1024 * 1024;
    const int MaxPackets = 500000;
    const long MaxTime = 36000L * 1000 * 1000 * 1000;

    // Corresponds to NicoMkvEvent in src/NicoBE.Mkv/reader.h.
    enum Event { RecordingTag = 1, TrackName, AssHeader, AssPacket, SubtitleCount }

    internal sealed class Track(string title)
    {
        public string Title { get; } = title;
        public StringBuilder? Document { get; set; }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate int Receive(nint context, Event kind, int track, long start, long duration, nint data, int length);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate int Cancel(nint context);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate int NativeRead([MarshalAs(UnmanagedType.LPUTF8Str)] string path, Receive receive, Cancel cancel, nint context);

    static readonly Lazy<NativeRead> ReadNative = new(LoadReader);

    [DllImport("kernel32.dll", EntryPoint = "LoadLibraryExW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern nint LoadLibraryEx(string path, nint file, uint flags);

    static NativeRead LoadReader()
    {
        const uint searchDllDirectory = 0x100;
        const uint searchSystem32 = 0x800;
        string path = Path.Combine(AppContext.BaseDirectory, "mkv", "NicoBE.Mkv.dll");
        nint library = LoadLibraryEx(path, 0, searchDllDirectory | searchSystem32);
        if (library == 0)
            throw new IOException("MKV読み取り用DLLを読み込めません。mkvフォルダを含めてNicoBEを展開し直してください。", new Win32Exception(Marshal.GetLastWin32Error()));
        try
        {
            return Marshal.GetDelegateForFunctionPointer<NativeRead>(NativeLibrary.GetExport(library, "nico_mkv_read"));
        }
        catch
        {
            NativeLibrary.Free(library);
            throw;
        }
    }

    public static (string RecordingTag, int SubtitleCount, Track[] Tracks) Read(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var tracks = new SortedDictionary<int, Track>();
        string recordingTag = "";
        int subtitleCount = 0, totalBytes = 0, packets = 0;
        ExceptionDispatchInfo? failure = null;

        int OnEvent(nint _, Event kind, int index, long start, long duration, nint data, int length)
        {
            // Exceptions must never unwind through FFmpeg's native frames.
            try
            {
                if (token.IsCancellationRequested) return 1;
                if (kind == Event.SubtitleCount)
                {
                    subtitleCount = index;
                    return 0;
                }
                if (length < 0 || length > MaxAssBytes - totalBytes)
                    throw new InvalidDataException("MKV内のASSが大きすぎます。");
                totalBytes += length;
                if (kind == Event.AssPacket && ++packets > MaxPackets)
                    throw new InvalidDataException("MKV内の字幕数が上限を超えました。");
                string value = length == 0 ? "" : Marshal.PtrToStringUTF8(data, length)!.TrimEnd('\0');
                switch (kind)
                {
                    case Event.RecordingTag:
                        recordingTag = value;
                        break;
                    case Event.TrackName:
                        tracks.Add(index, new(value));
                        break;
                    case Event.AssHeader:
                        tracks[index].Document = new StringBuilder(value)
                            .Append("\n[Events]\nFormat: Start, End, Style, Text\n");
                        break;
                    case Event.AssPacket:
                        if (tracks[index].Document is { } document)
                            AppendDialogue(document, value, start, duration);
                        break;
                }
                return 0;
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
                return 1;
            }
        }

        int result = ReadNative.Value(Path.GetFullPath(path), OnEvent, _ => token.IsCancellationRequested ? 1 : 0, 0);
        token.ThrowIfCancellationRequested();
        failure?.Throw();
        if (result < 0) throw new InvalidDataException($"MKVから字幕を読み込めませんでした（エラー {result}）。");
        return (recordingTag, subtitleCount, tracks.Values.ToArray());
    }

    static void AppendDialogue(StringBuilder document, string packet, long start, long duration)
    {
        if (start < -MaxTime || start > MaxTime || duration <= 0 || duration > MaxTime) return;
        long end = start + duration;
        if (end <= 0 || end > MaxTime || packet.Length > 65536) return;
        // Matroska stores the ASS timing on the block. The first two fields
        // in the payload are ReadOrder and Layer (Marked for SSA).
        string[] fields = packet.Split(',', 9);
        if (fields.Length != 9 || fields[2].Contains('\r') || fields[2].Contains('\n')) return;
        string content = fields[8].Replace("\r\n", "\\N").Replace("\r", "\\N").Replace("\n", "\\N");
        document.Append("Dialogue: ").Append(Stamp(Math.Max(0, start))).Append(',')
            .Append(Stamp(end)).Append(',').Append(fields[2]).Append(',').Append(content).Append('\n');
        if (document.Length > MaxAssBytes) throw new InvalidDataException("MKV内のASSが大きすぎます。");
    }

    static string Stamp(long microseconds)
    {
        long seconds = microseconds / 1_000_000;
        return FormattableString.Invariant($"{seconds / 3600}:{seconds / 60 % 60:00}:{seconds % 60:00}.{microseconds % 1_000_000:000000}");
    }
}
