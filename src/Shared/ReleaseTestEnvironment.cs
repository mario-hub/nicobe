using System.IO;
using System.Net.Http;

namespace NicoBE;

// Opt-in only for fresh executable copies made by tools/test-release.ps1.
internal static class ReleaseTestEnvironment
{
    internal static bool Requested => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NICOBE_RELEASE_TEST_ROOT"));
    internal static string? Root { get; } = Validate();
    static string? Validate()
    {
        string? value=Environment.GetEnvironmentVariable("NICOBE_RELEASE_TEST_ROOT");
        if(string.IsNullOrEmpty(value))return null;
        string path=Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
        if(!Path.IsPathFullyQualified(value) || !path.Equals(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory),StringComparison.OrdinalIgnoreCase)
            || !File.Exists(Path.Combine(path,".release-test")) || File.ReadAllText(Path.Combine(path,".release-test"))!="NicoBE isolated release test")
            throw new IOException("Invalid isolated release-test directory.");
        for(DirectoryInfo? d=new(path);d!=null;d=d.Parent)
            if((d.Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("Release-test directory must not contain reparse points.");
        return path;
    }
    internal sealed class OfflineHandler:HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
            =>throw new InvalidOperationException("External HTTP is disabled in release tests. Inject a mock handler.");
    }
}
