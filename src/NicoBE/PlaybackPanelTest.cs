using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NicoBE;

// Synthetic commands and an off-screen owner only; never controls a user's player.
internal static class PlaybackPanelTest
{
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern nint CreateWindowEx(uint ex,string cls,string title,uint style,int x,int y,int width,int height,nint parent,nint menu,nint instance,nint param);
    [DllImport("user32.dll")]static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll",EntryPoint="SendMessageW")]static extern nint SendMessage(nint window,uint message,nint wParam,nint lParam);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern bool SetWindowText(nint window,string text);
    public static int Run()
    {
        var checks=new List<string>();PlaybackPanelWindow? window=null;nint host=0,cover=0;
        void Check(bool condition,string label){if(!condition)throw new Exception(label);checks.Add(label);}
        var culture=CultureInfo.CurrentCulture;
        var context=SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
        try
        {
            void Complete(Task task)
            {
                var frame=new System.Windows.Threading.DispatcherFrame();
                var dispatcher=System.Windows.Threading.Dispatcher.CurrentDispatcher;
                task.ContinueWith(_=>dispatcher.BeginInvoke(()=>frame.Continue=false));
                if(!task.IsCompleted)System.Windows.Threading.Dispatcher.PushFrame(frame);
                task.GetAwaiter().GetResult();
            }
            var rateParser=new MpcPlaybackRate();
            Check(rateParser.Parse("再生中 (1.75x) [DXVA]")==1.75 && rateParser.Parse("再生中")==1,"status rate and normal speed parse");
            Check(rateParser.Parse("Paused")==null && rateParser.Parse("Buffering 50%")==null,"pause and transient status never imply normal speed");
            Check(rateParser.Parse("Lecture (1,25x)")==1.25 && rateParser.Parse("Lecture")==1,"localized label is learned from a rate suffix");
            rateParser.Reset();Check(rateParser.Parse("Lecture")==null,"reset discards the previous player label");
            var first=new List<(uint,string)>();var second=new List<(uint,string)>();bool accept=true;
            using var panel=new PlaybackPanel((command,value)=>{first.Add((command,value));return accept;});
            using var other=new PlaybackPanel((command,value)=>{second.Add((command,value));return true;});
            CultureInfo.CurrentCulture=CultureInfo.GetCultureInfo("de-DE");
            int settingsOpened=0;
            window=new PlaybackPanelWindow(panel.ChangeSpeedAsync,panel.JumpAsync,()=>settingsOpened++);
            window.SettingsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(settingsOpened==1,"gear button requests settings");
            foreach(var button in window.SpeedButtons.Values.Concat(window.JumpButtons.Values).Append(window.SettingsButton))
            {
                button.ApplyTemplate();
                var chrome=(Border)button.Template.FindName("chrome",button);
                var fill=chrome.Background;
                var pressed=typeof(System.Windows.Controls.Primitives.ButtonBase).GetProperty("IsPressed")!.GetSetMethod(true)!;
                try
                {
                    pressed.Invoke(button,new object[]{true});
                    Check(button.IsPressed && Equals(chrome.Background,fill),"press keeps panel button fill: "+button.Content);
                }
                finally{pressed.Invoke(button,new object[]{false});}
            }
            var pending=new TaskCompletionSource<bool>();int sends=0;
            var busyWindow=new PlaybackPanelWindow(_=>{sends++;return pending.Task;},_=>Task.FromResult(true));
            try
            {
                busyWindow.SpeedButtons[1.5].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(busyWindow.SpeedButtons.Values.Concat(busyWindow.JumpButtons.Values).All(b=>b.IsEnabled),"pending command does not fade the buttons through disabled styling");
                busyWindow.SpeedButtons[2].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(sends==1,"repeat commands are guarded without changing button appearance");
                pending.SetResult(true);
                System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(()=>{},System.Windows.Threading.DispatcherPriority.ContextIdle);
                Check(busyWindow.SpeedButtons[1.5].FontWeight==FontWeights.Bold,"completed command updates the selected speed");
            }
            finally{busyWindow.Close();}
            foreach(int seconds in new[]{-30,-10,-5,5,10,30})window.JumpButtons[seconds].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(first.SequenceEqual(new[]{"-30","-10","-5","5","10","30"}.Select(s=>(MpcClient.JumpSeconds,s))),"signed relative-seek commands preserve every rapid click");
            first.Clear();
            foreach(double speed in new[]{.5,1,1.25,1.5,1.75,2,2.5})window.SpeedButtons[speed].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(first.SequenceEqual(new[]{"0.5","1","1.25","1.5","1.75","2","2.5"}.Select(s=>(MpcClient.SetSpeed,s))),"all seven speeds use invariant API values");
            Check(panel.SelectedSpeed==2.5 && window.SpeedButtons.Count(b=>b.Value.FontWeight==FontWeights.Bold)==1 && window.SpeedButtons[2.5].FontWeight==FontWeights.Bold,"only the selected speed is highlighted");
            window.SpeedButtons[2.5].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(panel.SelectedSpeed==2.5 && window.SpeedButtons[2.5].FontWeight==FontWeights.Bold,"clicking the selected speed does not toggle it off");
            other.ChangeSpeedAsync(1.25).GetAwaiter().GetResult();other.JumpAsync(-5).GetAwaiter().GetResult();
            Check(second.SequenceEqual(new[]{(MpcClient.SetSpeed,"1.25"),(MpcClient.JumpSeconds,"-5")}) && panel.SelectedSpeed==2.5,"multiple players keep command routing and selections separate");
            accept=false;window.SpeedButtons[1].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(panel.SelectedSpeed==2.5 && window.SpeedButtons[2.5].FontWeight==FontWeights.Bold,"failed sends retain the previous selection");
            accept=true;window.SpeedButtons[1.25].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(!panel.ChangeSpeedAsync(3).GetAwaiter().GetResult() && !panel.JumpAsync(100).GetAwaiter().GetResult(),"unsupported values cannot be sent");
            var hover=new PanelHover();
            Check(hover.Update(true,false,0),"activation reveals the panel on the first polling tick without dwell");
            Check(hover.Update(false,true,.5) && !hover.Update(true,false,.51),"leaving the actual panel hides on the next poll even inside the activation area");
            hover.Update(true,false,2);hover.Update(true,false,2.2);hover.Reset();Check(!hover.Visible,"focus loss or closing resets visibility immediately");
            var bounds=PlaybackPanelWindow.Bounds(new Rect(-1920,0,1920,1080),new Size(822,153),18);
            Check(bounds.Left==-1902 && bounds.Bottom==1062 && bounds.Width==822,"placement supports negative monitor coordinates and 150 percent DPI");
            var activation=PlaybackPanel.ActivationBounds(new Rect(0,0,1920,1080),1);
            Check(activation==new Rect(12,964,548,104),"activation area approximates the full two-row panel");
            activation=PlaybackPanel.ActivationBounds(new Rect(-1920,0,531,1080),1.5);
            Check(activation.Width==495 && activation.Height==408 && activation.Left==-1902 && activation.Bottom==1062,"activation area follows wrapping and monitor DPI");
            void Render(int width,string name)
            {
                var content=(FrameworkElement)window.Content;content.Measure(new Size(width,double.PositiveInfinity));
                int height=(int)Math.Ceiling(content.DesiredSize.Height);content.Arrange(new Rect(0,0,width,height));content.UpdateLayout();
                foreach(var button in window.SpeedButtons.Values.Concat(window.JumpButtons.Values).Append(window.SettingsButton))
                {var point=button.TranslatePoint(new Point(),content);if(point.X<0 || point.X+button.ActualWidth>width || button.ActualWidth<60)throw new Exception("Panel button clipped");}
                var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);bitmap.Render(content);
                var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using var output=File.Create(Path.Combine(Paths.Logs,name));png.Save(output);
                checks.Add($"{width}px layout has readable unclipped buttons");
            }
            Render(548,"playback-panel.png");Render(330,"playback-panel-narrow.png");
            nint foreground=Native.GetForegroundWindow();
            host=CreateWindowEx(0x08000080,"STATIC","NicoBE panel test",0x90000000,-32000,-32000,1280,720,0,0,0,0);
            Check(host!=0,"off-screen test owner created");
            nint video=CreateWindowEx(0,"STATIC","video",0x50000000,0,0,1280,720,host,59648,0,0);
            Check(video!=0,"off-screen video surface created");
            cover=CreateWindowEx(0x08000080,"STATIC","unrelated test window",0x90000000,-32000,-32000,200,200,0,0,0,0);
            Check(cover!=0,"off-screen covering window created without activation");
            bool IsAbove(nint above,nint below)
            {
                for(nint current=Native.GetWindow(below,3);current!=0;current=Native.GetWindow(current,3))if(current==above)return true;
                return false;
            }
            window.Place(host,new Rect(-32000,-32000,1280,720),1);
            Check(Native.GetWindow(window.Handle,4)==host && (Native.GetWindowLong(window.Handle,-20)&(nint)0x08000080)==(nint)0x08000080,"panel is owned, non-activating and absent from the taskbar");
            Check(Native.GetForegroundWindow()==foreground,"showing the off-screen panel does not steal foreground focus");
            Check(IsAbove(cover,window.Handle) && IsAbove(window.Handle,host),"panel stays above its player and below unrelated windows");
            Check(SendMessage(window.Handle,0x21,host,(nint)((0x201<<16)|1))==3,"native mouse activation handler accepts clicks without activation");
            window.SpeedButtons[1.75].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));window.JumpButtons[-5].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(Native.GetForegroundWindow()==foreground && panel.SelectedSpeed==1.75,"speed and seek button actions preserve foreground focus");
            window.Hide();
            int hoverSettings=0;
            using(var hovering=new PlaybackPanel((_,_)=>true,()=>hoverSettings++))
            {
                PlaybackPanelWindow? HoverWindow()=>(PlaybackPanelWindow?)typeof(PlaybackPanel).GetField("window",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(hovering);
                var pointer=new Native.Point {X=-31960,Y=-31330};
                Check(Native.GetForegroundWindow()!=host,"test owner has no foreground focus");
                hovering.TickAt(host,true,0,pointer,video);
                Check(HoverWindow()?.IsVisible==true && Native.GetForegroundWindow()==foreground,"hover reveals panel for a background player without activation");
                HoverWindow()!.SettingsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(hoverSettings==1 && HoverWindow()!.IsVisible,"gear click requests settings without briefly hiding the panel");
                Check(IsAbove(cover,HoverWindow()!.Handle),"hover reveal does not raise panel above unrelated windows");
                var statusBar=CreateWindowEx(0,"STATIC","",0x50000000,0,0,200,20,host,10016,0,0);
                var statusLabel=CreateWindowEx(0,"STATIC","Playing (1.75x)",0x50000000,0,0,200,20,statusBar,10626,0,0);
                Check(statusLabel!=0,"mock MPC status label created");
                // Finish any read started by TickAt, then request the changed status.
                var wait=System.Diagnostics.Stopwatch.StartNew();
                while((bool)typeof(PlaybackPanel).GetField("readingSpeed",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(hovering)!)
                {if(wait.Elapsed.TotalSeconds>2)throw new Exception("Status read did not finish");Complete(Task.Delay(5));}
                Complete(hovering.RefreshSpeedAsync(host));
                Check(hovering.SelectedSpeed==1.75 && HoverWindow()!.SpeedButtons[1.75].FontWeight==FontWeights.Bold,"external speed change updates selection");
                SetWindowText(statusLabel,"Playing");Complete(hovering.RefreshSpeedAsync(host));
                Check(hovering.SelectedSpeed==1,"external reset to normal speed updates selection");
                SetWindowText(statusLabel,"Playing (2.25x)");Complete(hovering.RefreshSpeedAsync(host));
                Check(hovering.SelectedSpeed==2.25 && HoverWindow()!.SpeedButtons.Values.All(b=>b.FontWeight!=FontWeights.Bold),"unlisted speed clears all preset highlights");
                SetWindowText(statusLabel,"Paused");Complete(hovering.RefreshSpeedAsync(host));
                Check(hovering.SelectedSpeed==2.25,"pause preserves the known speed");
                hovering.TickAt(host,true,.3,pointer,cover);
                Check(HoverWindow()?.IsVisible==false,"unrelated window under pointer prevents continued display");
                hovering.TickAt(host,true,.4,pointer,cover);
                Check(HoverWindow()?.IsVisible==false,"hidden player areas do not trigger a panel");
                hovering.TickAt(host,true,.5,pointer,video);
                Check(HoverWindow()?.IsVisible==true && IsAbove(cover,HoverWindow()!.Handle),"repeat hover preserves ownership and z-order");
                hovering.Enabled=false;
                Check(HoverWindow()==null && Native.GetForegroundWindow()==foreground,"disabling background panel releases its window without activation");
            }
            typeof(PlaybackPanel).GetField("window",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(panel,window);
            nint panelHandle=window.Handle;double? selected=panel.SelectedSpeed;
            panel.Enabled=false;
            Check(!Native.IsWindow(panelHandle) && panel.SelectedSpeed==selected,"disabling the panel immediately closes its window without changing playback selection");
            panel.Enabled=true;
            Check(panel.SelectedSpeed==selected,"reenabling keeps the last selected speed");
            panel.Reset();Check(panel.SelectedSpeed==null,"file changes clear previous selection");
            panel.Dispose();Check(!panel.ChangeSpeedAsync(1).GetAwaiter().GetResult() && !panel.JumpAsync(5).GetAwaiter().GetResult() && other.ChangeSpeedAsync(2).GetAwaiter().GetResult(),"closing one panel leaves the other usable");
            Paths.WriteJson("playback-panel-test.json",new{passed=true,checks,realPlayerControlled=false});return 0;
        }
        catch(Exception ex){Paths.WriteJson("playback-panel-test.json",new{passed=false,checks,error=ex.ToString()});return 1;}
        finally{SynchronizationContext.SetSynchronizationContext(context);CultureInfo.CurrentCulture=culture;window?.Close();if(cover!=0)DestroyWindow(cover);if(host!=0)DestroyWindow(host);}
    }
}
