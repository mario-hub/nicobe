using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace NicoBE.Encoder;

// ISDB audio_component_descriptor (ARIB B10, tag 0xC4), joined to the
// PMT stream_identifier_descriptor. Channel count alone cannot identify dual mono.
internal static class DualMonoAudio
{
    internal sealed class Tables(RecordingInfo recording)
    {
        readonly Dictionary<int, int> components = new();
        readonly Dictionary<int, bool> japaneseMain = new();
        readonly HashSet<int> ambiguous = new();
        public int PmtPid { get; private set; } = -1;
        public Dictionary<int, bool> Result => components.Where(c => japaneseMain.ContainsKey(c.Key) && !ambiguous.Contains(c.Key))
            .GroupBy(c => c.Value).Where(g => g.Select(c => japaneseMain[c.Key]).Distinct().Count() == 1)
            .ToDictionary(g => g.Key, g => japaneseMain[g.First().Key]);

        public void Consume(int pid, byte[] s)
        {
            if (s.Length < 12 || Crc(s) != 0 || (s[5] & 1) == 0) return;
            int service = (s[3] << 8) | s[4];
            if (pid == 0 && s[0] == 0)
            {
                for (int i = 8; i + 4 <= s.Length - 4; i += 4)
                    if (((s[i] << 8) | s[i + 1]) == recording.ServiceId) PmtPid = ((s[i + 2] & 31) << 8) | s[i + 3];
            }
            else if (pid == PmtPid && s[0] == 2 && service == recording.ServiceId)
            {
                for (int i = 12 + ((s[10] & 15) << 8) + s[11]; i + 5 <= s.Length - 4;)
                {
                    int end = i + 5 + ((s[i + 3] & 15) << 8) + s[i + 4];
                    if (end > s.Length - 4) return;
                    if (s[i] == 0x0f) // ADTS AAC; dual_mono_mode is not a general stereo filter.
                        for (int d = i + 5; d + 2 <= end;)
                        {
                            int next = d + 2 + s[d + 1];
                            if (next > end) break;
                            if (s[d] == 0x52 && s[d + 1] == 1) components[s[d + 2]] = ((s[i + 1] & 31) << 8) | s[i + 2];
                            d = next;
                        }
                    i = end;
                }
            }
            else if (pid == 0x12 && s[0] == 0x4e && service == recording.ServiceId && s.Length >= 18)
            {
                // Present/following only, and only events intersecting this recording.
                for (int i = 14; i + 12 <= s.Length - 4;)
                {
                    int end = i + 12 + ((s[i + 10] & 15) << 8) + s[i + 11];
                    if (end > s.Length - 4) return;
                    var start = Date(s.AsSpan(i + 2, 5));
                    int h = Bcd(s[i + 7]), m = Bcd(s[i + 8]), sec = Bcd(s[i + 9]);
                    int duration = h >= 0 && m is >= 0 and < 60 && sec is >= 0 and < 60 ? h * 3600 + m * 60 + sec : 0;
                    if (start is {} time && duration > 0 && time < recording.End && time.AddSeconds(duration) > recording.Start)
                        for (int d = i + 12; d + 2 <= end;)
                        {
                            int next = d + 2 + s[d + 1];
                            if (next > end) break;
                            var body = s.AsSpan(d + 2, s[d + 1]);
                            if (s[d] == 0xc4 && body.Length >= 12 && (body[0] & 15) == 2 && body[1] == 2 && body[3] == 0x0f && (body[5] & 0x80) != 0)
                            {
                                string first = Encoding.ASCII.GetString(body.Slice(6, 3)), second = Encoding.ASCII.GetString(body.Slice(9, 3));
                                if (first == "jpn" && second == "eng" || first == "eng" && second == "jpn")
                                {
                                    bool main = first == "jpn";
                                    if (japaneseMain.TryGetValue(body[2], out bool previous) && previous != main) ambiguous.Add(body[2]);
                                    japaneseMain[body[2]] = main;
                                }
                            }
                            d = next;
                        }
                    i = end;
                }
            }
        }
    }

    public static Dictionary<int, bool> Detect(string path, RecordingInfo recording, JsonElement[] streams, CancellationToken token)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1024 * 1024);
        var head = new byte[(int)Math.Min(file.Length, 32 * 1024 * 1024)]; file.ReadExactly(head);
        var (stride, origin) = TsReader.FindPackets(head);
        var tables = new Tables(recording);
        void Scan(byte[] data, int first)
        {
            var sections = new Dictionary<int, TsReader.Section>();
            for (int offset = first; offset + 188 <= data.Length; offset += stride)
            {
                if ((offset / stride & 4095) == 0) token.ThrowIfCancellationRequested();
                var p = data.AsSpan(offset, 188);
                if (p[0] != 0x47 || (p[1] & 0x80) != 0 || (p[3] & 0xc0) != 0 || (p[3] & 0x10) == 0) continue;
                int pid = ((p[1] & 31) << 8) | p[2];
                if (pid != 0 && pid != 0x12 && pid != tables.PmtPid) continue;
                int payload = (p[3] & 0x20) != 0 ? 5 + p[4] : 4;
                if (payload >= 188) continue;
                if (!sections.TryGetValue(pid, out var section)) sections[pid] = section = new();
                section.Feed(p[payload..], (p[1] & 0x40) != 0, p[3] & 15, s => tables.Consume(pid, s));
            }
        }
        Scan(head, origin);
        // Bounded reads also cover a program change after the recording pre-roll.
        if (file.Length > head.Length)
            foreach (long target in new[] { file.Length / 2, Math.Max(head.Length, file.Length - 16 * 1024 * 1024) }.Distinct())
            {
                file.Position = origin + Math.Max(0, (target - origin) / stride) * stride;
                var window = new byte[(int)Math.Min(16 * 1024 * 1024, file.Length - file.Position)]; file.ReadExactly(window); Scan(window, 0);
            }
        var pids = tables.Result;
        var result = new Dictionary<int, bool>();
        foreach (var stream in streams)
            if (stream.TryGetProperty("codec_name", out var codec) && codec.GetString() == "aac" && stream.TryGetProperty("id", out var id)
                && int.TryParse((id.GetString() ?? "").Replace("0x", ""), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int pid)
                && pids.TryGetValue(pid, out bool main)) result[stream.GetProperty("index").GetInt32()] = main;
        return result;
    }

    internal static uint Crc(ReadOnlySpan<byte> bytes)
    {
        uint crc = 0xffffffff;
        foreach (byte value in bytes)
        {
            crc ^= (uint)value << 24;
            for (int bit = 0; bit < 8; bit++) crc = (crc & 0x80000000) != 0 ? (crc << 1) ^ 0x04c11db7 : crc << 1;
        }
        return crc;
    }
    static int Bcd(byte value) => (value >> 4) <= 9 && (value & 15) <= 9 ? (value >> 4) * 10 + (value & 15) : -1000000;
    static DateTimeOffset? Date(ReadOnlySpan<byte> value)
    {
        int h = Bcd(value[2]), m = Bcd(value[3]), s = Bcd(value[4]);
        if (h is < 0 or > 23 || m is < 0 or > 59 || s is < 0 or > 59) return null;
        return new DateTimeOffset(1858, 11, 17, 0, 0, 0, TimeSpan.FromHours(9)).AddDays((value[0] << 8) | value[1]).AddHours(h).AddMinutes(m).AddSeconds(s);
    }
}
