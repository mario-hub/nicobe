using System.IO;
using Microsoft.Win32;

namespace NicoBE;

public interface IStartupRegistration
{
    string? Read();
    void Write(string? command);
}
public sealed class WindowsStartupRegistration : IStartupRegistration
{
    const string Key=@"Software\Microsoft\Windows\CurrentVersion\Run";
    public string? Read(){using var key=Registry.CurrentUser.OpenSubKey(Key,false);return key?.GetValue("NicoBE") as string;}
    public void Write(string? command)
    {
        using var key=Registry.CurrentUser.CreateSubKey(Key,true);
        if(command==null)key.DeleteValue("NicoBE",false);else key.SetValue("NicoBE",command,RegistryValueKind.String);
    }
}
public static class SettingsCommit
{
    public static string StartupCommand=>"\""+Path.Combine(AppContext.BaseDirectory,"NicoBE.exe")+"\"";
    // The Windows startup key is touched only after the user changes this toggle
    // and saves. Tests inject an in-memory registration service.
    public static void Save(Config candidate,bool enabled,bool originalEnabled,IStartupRegistration startup,Action<Config>? persist=null)
    {
        candidate.Validate(false);candidate.SetupCompleted=true;
        bool changed=enabled!=originalEnabled;string? old=changed?startup.Read():null;
        if(changed)startup.Write(enabled?StartupCommand:null);
        try{if(persist!=null)persist(candidate);else candidate.Save();}
        catch{if(changed)startup.Write(old);throw;}
    }
}
