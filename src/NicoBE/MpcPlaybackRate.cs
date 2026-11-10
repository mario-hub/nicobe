using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace NicoBE;

// MPC's slave API exposes SetSpeed but not GetSpeed. Read the existing status
// label instead. Keep this independent of the localized state text where possible.
internal sealed class MpcPlaybackRate
{
    string? playingLabel;
    static readonly Regex rateSuffix=new(@"^(.+?)\s+\(([0-9]+(?:[.,][0-9]+)?)[x×]\)$",RegexOptions.CultureInvariant);
    static readonly Regex hardwareSuffix=new(@"\s+\[[^\]]*\]$",RegexOptions.CultureInvariant);
    internal void Reset()=>playingLabel=null;
    internal double? Parse(string text)
    {
        text=hardwareSuffix.Replace(text.Trim(),"");
        var match=rateSuffix.Match(text);
        if(match.Success && double.TryParse(match.Groups[2].Value.Replace(',','.'),NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out double rate)
            && rate>0 && double.IsFinite(rate))
        {
            playingLabel=match.Groups[1].Value;
            return rate;
        }
        // A pause, a menu hint or an error without a rate must not imply 1x.
        return text is "再生中" or "Playing" || (playingLabel!=null && text==playingLabel)?1:null;
    }
    internal static Task<string> ReadAsync(nint player)=>Task.Run(()=>
    {
        nint bar=Native.GetDlgItem(player,10016),label=Native.GetDlgItem(bar,10626);
        if(label==0 || Native.GetAncestor(label,2)!=player)return "";
        var text=new StringBuilder(256);
        // WM_GETTEXT is marshalled by Windows. A stalled player must not block
        // the UI or create a queue of requests; the controller allows one at a time.
        return SendMessageTimeout(label,0x000D,(nuint)text.Capacity,text,0x22,75,out _)!=0?text.ToString():"";
    });
    [DllImport("user32.dll",EntryPoint="SendMessageTimeoutW",CharSet=CharSet.Unicode)]
    static extern nint SendMessageTimeout(nint window,uint message,nuint wParam,StringBuilder text,uint flags,uint timeout,out nint result);
}
