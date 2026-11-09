using System.Diagnostics;
using System.IO;

namespace NicoBE;

public static class PlayerLocator
{
    static readonly string[] Names={"mpc-be64","mpc-be"};
    public static IEnumerable<Process> Candidates()=>Names.SelectMany(Process.GetProcessesByName);
    public static bool IsPlayerExecutable(string? path)
    {
        if(string.IsNullOrWhiteSpace(path)||!File.Exists(path))return false;
        try
        {
            var version=FileVersionInfo.GetVersionInfo(path);
            string product=version.ProductName??"";
            return (product.Equals("MPC-BE",StringComparison.OrdinalIgnoreCase)||product.StartsWith("MPC-BE ",StringComparison.OrdinalIgnoreCase))
                && Names.Any(n=>string.Equals(version.OriginalFilename,n+".exe",StringComparison.OrdinalIgnoreCase));
        }
        catch(IOException){return false;}
    }
    public static string? Executable(Process process)
    {
        try
        {
            string? path=process.MainModule?.FileName;
            return IsPlayerExecutable(path)?Native.CanonicalPath(path!):null;
        }
        catch(System.ComponentModel.Win32Exception){return null;}
        catch(InvalidOperationException){return null;}
        catch(IOException){return null;}
    }
}
