using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace NicoBE;

// Off-screen test host only. Never attaches to or changes a user's MPC-BE.
internal static class VisibilityTest
{
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    static extern nint CreateWindowEx(uint ex,string cls,string title,uint style,int x,int y,int w,int h,nint parent,nint menu,nint instance,nint param);
    [DllImport("user32.dll")] static extern bool DestroyWindow(nint window);
    static T Field<T>(object value,string name)=>(T)value.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(value)!;
    public static int Run()
    {
        var checks=new List<string>();MainWindow? session=null;nint host=0;
        void Check(bool valid,string name){if(!valid)throw new Exception(name);checks.Add(name);}
        void Wait(Func<bool> condition,string name)
        {
            var timeout=Stopwatch.StartNew();
            // No managed dispatcher pumping: the visibility setter must publish
            // without depending on the next player-position polling tick.
            while(!condition()){if(timeout.ElapsedMilliseconds>1500)throw new Exception(name);Thread.Sleep(15);}
            checks.Add(name);
        }
        nint OwnedOverlay()
        {
            nint found=0;
            Native.EnumWindows((window,_)=>
            {
                if(Native.GetWindow(window,4)!=host)return true;
                var name=new StringBuilder(128);Native.GetClassName(window,name,name.Capacity);
                if(name.ToString()!="NicoBEGpuOverlay")return true;
                found=window;return false;
            },0);return found;
        }
        try
        {
            nint foreground=Native.GetForegroundWindow();
            host=CreateWindowEx(0x08000080,"STATIC","NicoBE off-screen test",0x90000000,-32000,-32000,640,360,0,0,0,0);
            Check(host!=0,"create off-screen host");
            Check(CreateWindowEx(0,"STATIC","video",0x50000000,0,0,640,360,host,59648,0,0)!=0,"create video surface");
            session=new MainWindow(new Config {CommentsVisible=false});session.InitializeHidden();
            var client=Field<MpcClient>(session,"mpc");
            typeof(MpcClient).GetProperty(nameof(MpcClient.PlayerWindow))!.SetValue(client,host);
            var engine=Field<CommentEngine>(session,"engine");
            engine.SetComments(new[]{new Comment(5,"表示切り替え","#FFFFFF",1,CommentPosition.Scroll)});engine.OnSeek(5,0);
            void Toggle(bool enabled)
            {
                typeof(MainWindow).GetField("lastPositionWall",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(session,Field<Stopwatch>(session,"wall").Elapsed.TotalSeconds);
                session.CommentsVisible=enabled;
            }
            Toggle(true);
            Wait(()=>OwnedOverlay() is var window && window!=0 && Native.IsWindowVisible(window),"off to on without foreground focus or polling");
            Check(Native.GetWindow(host,3)==OwnedOverlay(),"overlay placed directly above host");
            Check(session.VisibleComments.Count==1,"paused comments restored on enable");
            Toggle(false);Wait(()=>!Native.IsWindowVisible(OwnedOverlay()),"on to off immediately");
            Toggle(true);Wait(()=>Native.IsWindowVisible(OwnedOverlay()),"off to on again while paused");
            Check(Native.GetWindow(host,3)==OwnedOverlay(),"restored overlay remains above host");
            Check(Native.GetForegroundWindow()==foreground,"foreground focus unchanged");
            Paths.WriteJson("visibility-test.json",new{passed=true,checks});return 0;
        }
        catch(Exception ex){Paths.WriteJson("visibility-test.json",new{passed=false,checks,error=ex.ToString()});return 1;}
        finally{session?.ShutdownSession();if(host!=0)DestroyWindow(host);}
    }
}
