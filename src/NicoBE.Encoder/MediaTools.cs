using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace NicoBE;

public static class MediaTools
{
    public static string Capture(string executable, IEnumerable<string> arguments, CancellationToken token, int limit = 64 * 1024 * 1024)
        => CaptureAsync(executable, arguments, token, limit).GetAwaiter().GetResult();

    static async Task<string> CaptureAsync(string executable, IEnumerable<string> arguments, CancellationToken token, int limit)
    {
        token.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Paths.Root
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("FFmpegを起動できません。");
        void Stop() { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { } }
        using var cancel = token.Register(Stop);
        try
        {
            var error = ReadBounded(process.StandardError, 1024 * 1024, token);
            var output = ReadBounded(process.StandardOutput, limit, token);
            // A fault in either pipe must terminate the process before waiting for the other.
            _ = error.ContinueWith(_ => Stop(), CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            _ = output.ContinueWith(_ => Stop(), CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            await Task.WhenAll(output, error);
            await process.WaitForExitAsync(token);
            if (process.ExitCode != 0) throw new IOException("字幕情報を取得できません: " + error.Result[^Math.Min(1600, error.Result.Length)..]);
            return output.Result;
        }
        finally { Stop(); }
    }

    static async Task<string> ReadBounded(StreamReader reader, int limit, CancellationToken token)
    {
        var text = new StringBuilder();
        var buffer = new char[8192];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) > 0)
        {
            if (text.Length + count > limit) throw new InvalidDataException("字幕データが読み込み上限を超えました。");
            text.Append(buffer, 0, count);
        }
        return text.ToString();
    }

    public static JsonDocument Probe(string ffmpeg, string file, CancellationToken token)
        => JsonDocument.Parse(Capture(Path.Combine(Path.GetDirectoryName(ffmpeg)!, "ffprobe.exe"),
            new[] { "-v", "error", "-show_streams", "-show_format", "-of", "json", file }, token, 4 * 1024 * 1024));
}
