using System.IO;

namespace NicoBE;

public sealed record RecordingInfo(string Path, int ServiceId, int NetworkId, int PcrPid,
    DateTimeOffset Start, DateTimeOffset End, double PcrOffsetSeconds, double FirstPcrSeconds=0);

public static class TsReader
{
    private const double PcrWrap = 8589934592d;
    private sealed record Clock(long Offset, DateTimeOffset Date, Dictionary<int, long> Pcr);
    private sealed class Scan
    {
        public SortedDictionary<int, int> Programs = new();
        public Dictionary<int, (int Pcr, bool Video)> Pmts = new();
        public Dictionary<int, long> FirstPcr = new(), LatestPcr = new();
        public List<Clock> Clocks = new();
        public int Network;
        public Dictionary<int, Section> Sections = new();
    }
    internal sealed class Section
    {
        readonly List<byte> buffer = new();
        int lastCounter = -1;
        public void Feed(ReadOnlySpan<byte> data, bool start, int counter, Action<byte[]> consume)
        {
            if (lastCounter == counter) return;
            if (lastCounter >= 0 && ((lastCounter + 1) & 15) != counter) buffer.Clear();
            lastCounter = counter;
            if (start)
            {
                if (data.Length == 0 || data[0] + 1 > data.Length) { buffer.Clear(); return; }
                int pointer = data[0];
                if (buffer.Count > 0) { buffer.AddRange(data.Slice(1, pointer).ToArray()); Drain(consume); }
                buffer.Clear(); data = data[(1 + pointer)..];
            }
            else if (buffer.Count == 0) return;
            buffer.AddRange(data.ToArray()); Drain(consume);
        }
        void Drain(Action<byte[]> consume)
        {
            while (buffer.Count >= 3)
            {
                if (buffer[0] == 0xff) { buffer.Clear(); return; }
                int length = 3 + ((buffer[1] & 15) << 8) + buffer[2];
                if (length is < 3 or > 4096) { buffer.Clear(); return; }
                if (buffer.Count < length) return;
                consume(buffer.Take(length).ToArray()); buffer.RemoveRange(0, length);
            }
        }
    }

    public static RecordingInfo Read(string path, int? preferredService, CancellationToken token)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1024 * 1024);
        var head = new byte[(int)Math.Min(stream.Length, 32 * 1024 * 1024)];
        stream.ReadExactly(head);
        var (stride, origin) = FindPackets(head);
        var scan = new Scan(); ReadPackets(head, stride, origin, 0, scan, token);
        var candidates = scan.Programs.Keys.Where(s => scan.Pmts.TryGetValue(s, out var p) && p.Video).ToArray();
        int sid = preferredService ?? candidates.FirstOrDefault();
        if (!scan.Pmts.TryGetValue(sid, out var pmt)) throw new Exception("TSの映像サービスを判別できません。serviceIdの指定を確認してください。");
        if (!scan.FirstPcr.TryGetValue(pmt.Pcr, out long first)) throw new Exception("TSのPCRを取得できません。");
        var clock = scan.Clocks.FirstOrDefault(c => c.Pcr.ContainsKey(pmt.Pcr));
        if (clock == null) throw new Exception("TSに放送時刻(TOT/TDT)がありません。未編集の録画TSが必要です。");
        double elapsed = PcrDelta(clock.Pcr[pmt.Pcr], first) / 90000d;
        var beginning = clock.Date.AddSeconds(-elapsed);
        if (stream.Length > head.Length)
        {
            long offset = Math.Max(0, stream.Length - 16 * 1024 * 1024);
            offset = origin + Math.Max(0, (offset - origin + stride - 1) / stride) * stride;
            stream.Position = offset;
            var tail = new byte[(int)(stream.Length - offset)]; stream.ReadExactly(tail);
            var endScan = new Scan(); ReadPackets(tail, stride, 0, offset, endScan, token);
            scan.Clocks.AddRange(endScan.Clocks);
        }
        var end = scan.Clocks.Max(c => c.Date).AddSeconds(30);
        if (end < beginning) throw new Exception("TSの開始・終了時刻が逆転しています。");
        return new(path, sid, scan.Network, pmt.Pcr, beginning, end, elapsed, first/90000d);
    }

    internal static double PcrDelta(long value, long origin) => (value - origin + PcrWrap) % PcrWrap;
    internal static (int Stride, int Origin) FindPackets(byte[] bytes)
    {
        foreach (int stride in new[] {188, 192, 204})
            for (int offset = 0; offset < Math.Min(stride, bytes.Length); offset++)
                if (Enumerable.Range(0, 8).All(i => offset + stride * i < bytes.Length && bytes[offset + stride * i] == 0x47)) return (stride, offset);
        throw new Exception("MPEG-TSのパケットを認識できません。");
    }
    internal static DateTimeOffset ReadGrowingEnd(RecordingInfo info,CancellationToken token)
    {
        using var stream=new FileStream(info.Path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
        long length=stream.Length;
        var head=new byte[(int)Math.Min(length,4096)];stream.ReadExactly(head);
        var (stride,origin)=FindPackets(head);
        long offset=Math.Max(origin,length-1024*1024);
        offset=origin+(offset-origin+stride-1)/stride*stride;
        var tail=new byte[(int)Math.Max(0,length-offset)];stream.Position=offset;stream.ReadExactly(tail);
        var scan=new Scan();ReadPackets(tail,stride,0,offset,scan,token);
        if(!scan.LatestPcr.TryGetValue(info.PcrPid,out long last))return info.End;
        var clock=scan.Clocks.LastOrDefault(c=>c.Pcr.ContainsKey(info.PcrPid));
        if(clock!=null)return clock.Date.AddSeconds(PcrDelta(last,clock.Pcr[info.PcrPid])/90000d);
        // Choose the PCR wrap nearest the previous end, also supporting recordings over 26 hours.
        double seconds=PcrDelta(last,(long)Math.Round(info.FirstPcrSeconds*90000))/90000d;
        double wrap=PcrWrap/90000d;
        seconds+=Math.Max(0,Math.Round(((info.End-info.Start).TotalSeconds-seconds)/wrap))*wrap;
        return info.Start.AddSeconds(seconds);
    }
    private static void ReadPackets(byte[] bytes, int stride, int origin, long fileOffset, Scan scan, CancellationToken token)
    {
        for (int offset = origin; offset + 188 <= bytes.Length; offset += stride)
        {
            if ((offset / stride & 4095) == 0) token.ThrowIfCancellationRequested();
            ReadOnlySpan<byte> p = bytes.AsSpan(offset, 188);
            if (p[0] != 0x47 || (p[1] & 0x80) != 0 || (p[3] & 0xc0) != 0) continue;
            int pid = ((p[1] & 31) << 8) | p[2], afc = (p[3] >> 4) & 3, payload = 4;
            if ((afc & 2) != 0)
            {
                if (p[4] > 183) continue;
                if (p[4] >= 7 && (p[5] & 0x10) != 0)
                {
                    long value = ((long)p[6] << 25) | ((long)p[7] << 17) | ((long)p[8] << 9) | ((long)p[9] << 1) | (uint)(p[10] >> 7);
                    scan.FirstPcr.TryAdd(pid, value); scan.LatestPcr[pid] = value;
                }
                payload = 5 + p[4];
            }
            if ((afc & 1) == 0 || payload >= 188) continue;
            if (pid != 0 && pid != 0x11 && pid != 0x14 && !scan.Programs.Values.Contains(pid)) continue;
            if (!scan.Sections.TryGetValue(pid, out var section)) scan.Sections[pid] = section = new();
            long absolute = fileOffset + offset;
            section.Feed(p[payload..], (p[1] & 0x40) != 0, p[3] & 15, s => Consume(s, pid, absolute, scan));
        }
    }
    private static void Consume(byte[] s, int pid, long offset, Scan scan)
    {
        if (s.Length < 8) return;
        if (pid == 0 && s[0] == 0)
        {
            for (int i = 8; i + 4 <= s.Length - 4; i += 4)
            {
                int sid = (s[i] << 8) | s[i + 1];
                if (sid != 0) scan.Programs[sid] = ((s[i + 2] & 31) << 8) | s[i + 3];
            }
        }
        else if (s[0] == 2 && s.Length >= 16)
        {
            int sid = (s[3] << 8) | s[4], pcr = ((s[8] & 31) << 8) | s[9];
            int i = 12 + ((s[10] & 15) << 8) + s[11]; bool video = false;
            while (i + 5 <= s.Length - 4)
            {
                video |= s[i] is 0x01 or 0x02 or 0x1b or 0x24;
                i += 5 + ((s[i + 3] & 15) << 8) + s[i + 4];
            }
            scan.Pmts[sid] = (pcr, video);
        }
        else if (pid == 0x11 && s[0] == 0x42 && s.Length > 10) scan.Network = (s[8] << 8) | s[9];
        else if (pid == 0x14 && s[0] is 0x70 or 0x73)
        {
            int mjd = (s[3] << 8) | s[4];
            int Bcd(byte n) => (n >> 4) * 10 + (n & 15);
            int h = Bcd(s[5]), m = Bcd(s[6]), sec = Bcd(s[7]);
            if (h > 23 || m > 59 || sec > 59) return;
            // ARIB terrestrial/BS broadcasts encode JST here, not UTC.
            var date = new DateTimeOffset(1858, 11, 17, 0, 0, 0, TimeSpan.FromHours(9)).AddDays(mjd).AddHours(h).AddMinutes(m).AddSeconds(sec);
            scan.Clocks.Add(new(offset, date, new(scan.LatestPcr)));
        }
    }
}
