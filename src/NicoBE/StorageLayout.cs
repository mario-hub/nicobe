using System.IO;

namespace NicoBE;

// Keep executable assets separate from writable data. Only Program Files installs
// use per-user storage; other locations retain the existing portable layout.
public sealed record StorageLayout(string ExecutableDirectory, bool UsesLocalAppData,
    string PlayerDirectory, string EncoderDirectory, string FfmpegDirectory)
{
    public static StorageLayout Current { get; } = Resolve(AppContext.BaseDirectory,
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetEnvironmentVariable("ProgramW6432") ?? "" });

    // Explicit inputs allow checking both layouts without writing to system folders.
    public static StorageLayout Resolve(string executableDirectory, string localAppData,
        IEnumerable<string> programFilesDirectories)
    {
        string executable = Path.TrimEndingDirectorySeparator(Path.GetFullPath(executableDirectory));
        bool installed = programFilesDirectories.Where(p => !string.IsNullOrWhiteSpace(p)).Any(p =>
        {
            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(p));
            return executable.Equals(root, StringComparison.OrdinalIgnoreCase)
                || executable.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        });
        if (!installed) return new(executable, false, executable, executable, Path.Combine(executable, "ffmpeg"));
        if (string.IsNullOrWhiteSpace(localAppData) || !Path.IsPathFullyQualified(localAppData))
            throw new IOException("ユーザーのLocalAppDataフォルダを取得できません。");
        string data = Path.Combine(Path.GetFullPath(localAppData), "NicoBE");
        return new(executable, true, Path.Combine(data, "player"), Path.Combine(data, "encoder"), Path.Combine(data, "ffmpeg"));
    }
}
