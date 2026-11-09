using System.IO;

namespace NicoBE;

internal static class StorageLayoutTest
{
    internal static void Verify(Action<bool, string> check)
    {
        string local = @"C:\Users\Test\AppData\Local";
        string[] roots = { @"C:\Program Files", @"C:\Program Files (x86)", @"E:\Applications", "" };
        foreach (string executable in new[] { @"C:\Program Files\NicoBE", @"c:\PROGRAM FILES\Encoder\",
            @"C:\Program Files (x86)\NicoBE", @"E:\Applications\NicoBE", @"C:\Program Files" })
        {
            var layout = StorageLayout.Resolve(executable, local, roots);
            check(layout.UsesLocalAppData && layout.PlayerDirectory == Path.Combine(local, "NicoBE", "player")
                && layout.EncoderDirectory == Path.Combine(local, "NicoBE", "encoder")
                && layout.FfmpegDirectory == Path.Combine(local, "NicoBE", "ffmpeg"), "per-user layout: " + executable);
            check(layout.ExecutableDirectory == Path.TrimEndingDirectorySeparator(Path.GetFullPath(executable)), "assets remain beside executable");
        }
        foreach (string executable in new[] { @"D:\Portable\NicoBE", @"C:\Program Files Backup\NicoBE",
            @"C:\Program Files (x86)-other\NicoBE", @"C:\Program Files\..\Portable\NicoBE" })
        {
            var layout = StorageLayout.Resolve(executable, local, roots);
            string full = Path.GetFullPath(executable);
            check(!layout.UsesLocalAppData && layout.PlayerDirectory == full && layout.EncoderDirectory == full
                && layout.FfmpegDirectory == Path.Combine(full, "ffmpeg"), "portable layout: " + executable);
        }
        check(Paths.Config == Path.Combine(StorageLayout.Current.PlayerDirectory, "nicobe.json"), "player settings location");
#if NICOBE_ENCODER
        check(Paths.Root == StorageLayout.Current.EncoderDirectory
            && Encoder.EncoderPaths.Settings == Path.Combine(Paths.Root, "encoder-settings.json")
            && FfmpegInstaller.InstallRoot == StorageLayout.Current.FfmpegDirectory, "GUI and CLI encoder storage wiring");
#else
        check(Paths.Root == StorageLayout.Current.PlayerDirectory
            && AppAssets.IconPath == Path.Combine(Paths.ExecutableDirectory, "assets", "nicobe.ico"), "player storage and asset wiring");
#endif
    }

    internal static int Run()
    {
        var checks = new List<string>();
        try
        {
            Verify((value, name) => { if (!value) throw new Exception(name); checks.Add(name); });
            Paths.WriteJson("storage-test.json", new { passed = true, checks, systemFoldersWritten = false });
            return 0;
        }
        catch (Exception ex) { Paths.WriteJson("storage-test.json", new { passed = false, checks, error = ex.ToString() }); return 1; }
    }
}
