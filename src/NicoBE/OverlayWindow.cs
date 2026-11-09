using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;

namespace NicoBE;

// WPF handles controls only. A native hardware thread renders all comment pixels.
public sealed class OverlayWindow
{
    readonly CommentEngine engine;
    readonly Config config;
    Typeface typeface;
    string renderedFont;
    readonly Dictionary<(string,double),(double Width,double Height)> widths=new();
    nint renderer;
    int nextId;
    record TextId(int Value);
    public OverlayWindow(CommentEngine engine,Config config)
    {
        this.engine=engine;this.config=config;
        renderedFont=config.FontFamily;
        typeface=new Typeface(new FontFamily(config.FontFamily),FontStyles.Normal,FontWeights.Bold,FontStretches.Normal);
        int hr=GpuNative.NicoCreate(config.FontFamily,(float)config.Opacity,out renderer);
        if(hr<0)throw new Exception($"GPU描画を初期化できません (0x{hr:X8})。Direct3D 11のハードウェア描画が必要です。");
        engine.Measure=Measure;
        TraceLog.Write("GPU hardware renderer: "+GetStats().Adapter+"; target=60 FPS");
    }
    public void RefreshFont()
    {
        if(string.Equals(renderedFont,config.FontFamily,StringComparison.OrdinalIgnoreCase))return;
        RecreateRenderer();
    }
    internal void RecreateRenderer()
    {
        var updated=new Typeface(new FontFamily(config.FontFamily),FontStyles.Normal,FontWeights.Bold,FontStretches.Normal);
        int hr=GpuNative.NicoCreate(config.FontFamily,(float)config.Opacity,out var replacement);
        if(hr<0)throw new Exception($"GPU描画を初期化できません (0x{hr:X8})。");
        if(renderer!=0)GpuNative.NicoDestroy(renderer);
        renderer=replacement;typeface=updated;renderedFont=config.FontFamily;widths.Clear();
    }
    (double Width,double Height,object? Visual) Measure(Comment comment)
    {
        var key=(comment.Text,config.FontSize*comment.SizeScale);
        if(!widths.TryGetValue(key,out var measured))
        {
            var text=new FormattedText(comment.Text,CultureInfo.GetCultureInfo("ja-JP"),FlowDirection.LeftToRight,typeface,key.Item2,Brushes.White,1);
            measured=(text.WidthIncludingTrailingWhitespace,text.Height);
            if(widths.Count>=512)widths.Clear();widths[key]=measured;
        }
        return (measured.Width,measured.Height,new TextId(++nextId));
    }
    public void Follow(nint player,bool enabled) {if(renderer!=0)GpuNative.NicoFollow(renderer,player,enabled?1:0);}
    public void Redraw(double lease=.65)
    {
        if(renderer==0)return;
        var items=new GpuNative.Item[engine.Active.Count];
        for(int i=0;i<items.Length;i++)
        {
            var a=engine.Active[i];var c=a.Comment;
            items[i]=new() {Id=((TextId)a.Visual!).Value,X=(float)engine.X(a),Y=(float)engine.Y(a),Size=(float)(config.FontSize*c.SizeScale),
                Color=uint.Parse(c.Color.AsSpan(1),NumberStyles.HexNumber,CultureInfo.InvariantCulture),
                Velocity=c.Position==CommentPosition.Scroll?(float)config.ScrollPixelsPerSecond:0,
                Remaining=c.Position==CommentPosition.Scroll?-1:(float)Math.Max(0,config.FixedCommentSeconds-(engine.MotionSeconds-a.Born)),Text=c.Text};
        }
        GpuNative.NicoSubmit(renderer,items,items.Length,engine.Playing?1:0,Math.Clamp(lease,0,.65));
    }
    public GpuNative.Stats GetStats(){GpuNative.NicoStats(renderer,out var stats);return stats;}
    public void Close()
    {
        if(renderer!=0){GpuNative.NicoDestroy(renderer);renderer=0;}
        widths.Clear();engine.Measure=c=>(c.Text.Length*28d,config.FontSize*1.4,null);
    }
}

public static class GpuNative
{
    const string Library="NicoBE.Gpu.dll";
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl)]internal static extern int NicoActiveRenderers();
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
    public struct Item {public int Id;public float X,Y,Size;public uint Color;public float Velocity,Remaining;[MarshalAs(UnmanagedType.LPWStr)]public string Text;}
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
    public struct Stats {public double Fps,CpuMs,CacheMs;public int Visible,Pending;public uint Error;public int Hardware;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)]public string Adapter;}
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
    public struct Benchmark {public double GpuMeanMs,GpuP95Ms,CpuMeanMs,ColdMs,PacedFps;public int Count,Width,Height,Frames;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)]public string Adapter;}
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl,CharSet=CharSet.Unicode)]public static extern int NicoCreate(string font,float opacity,out nint renderer);
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl)]public static extern void NicoDestroy(nint renderer);
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl)]public static extern void NicoFollow(nint renderer,nint player,int enabled);
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl)]public static extern void NicoSubmit(nint renderer,[In,MarshalAs(UnmanagedType.LPArray,SizeParamIndex=2)]Item[] items,int count,int moving,double lease);
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl)]public static extern void NicoStats(nint renderer,out Stats stats);
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl)]public static extern int NicoValidate(out int checks);
    [DllImport(Library,CallingConvention=CallingConvention.Cdecl,CharSet=CharSet.Unicode)]public static extern int NicoBEnchmark(int count,int width,int height,out Benchmark result,string? png);
    internal static int RunRecoveryTest()
    {
        int before=NicoActiveRenderers();var config=new Config();var engine=new CommentEngine(config);
        var overlay=new OverlayWindow(engine,config);
        try
        {
            overlay.RecreateRenderer();overlay.Redraw();
            if(NicoActiveRenderers()!=before+1 || overlay.GetStats().Error!=0)throw new Exception("GPU renderer replacement failed or leaked a renderer.");
        }
        finally{overlay.Close();}
        if(NicoActiveRenderers()!=before)throw new Exception("GPU renderer was not released.");
        Paths.WriteJson("gpu-recovery-test.json",new{passed=true,hiddenRendererReplacement=true,deviceLossInjected=false});return 0;
    }
    public static int RunBenchmark()
    {
        var results=new List<object>();
        // Creates the real DirectComposition target but never shows its HWND.
        int smoke=NicoCreate("Yu Gothic UI",.95f,out var hidden);
        if(smoke<0){Paths.WriteJson("gpu-benchmark.json",new{passed=false,hiddenComposition=$"0x{smoke:X8}"});return 1;}
        NicoStats(hidden,out var hardware);NicoDestroy(hidden);
        int validated=NicoValidate(out int pixelChecks);
        if(validated<0){Paths.WriteJson("gpu-benchmark.json",new{passed=false,pixelChecks,error=$"0x{validated:X8}"});return 1;}
        foreach(var c in new[]{(70,1920,1080),(140,1920,1080),(70,3840,2160)})
        {
            int hr=NicoBEnchmark(c.Item1,c.Item2,c.Item3,out var b,c.Item1==70 && c.Item2==1920?System.IO.Path.Combine(Paths.Logs,"gpu-preview.png"):null);
            results.Add(new {count=c.Item1,width=c.Item2,height=c.Item3,hresult=$"0x{hr:X8}",gpuMeanMs=b.GpuMeanMs,gpuP95Ms=b.GpuP95Ms,cpuMeanMs=b.CpuMeanMs,coldCacheMs=b.ColdMs,pacedFps=b.PacedFps,frames=b.Frames,adapter=b.Adapter});
            if(hr<0){Paths.WriteJson("gpu-benchmark.json",new{passed=false,results});return 1;}
        }
        Paths.WriteJson("gpu-benchmark.json",new{passed=true,kind="Hardware GPU offscreen; not desktop presentation FPS",hiddenComposition="passed without showing a window",hardware=hardware.Hardware==1,pixelChecks,results});return 0;
    }
}
