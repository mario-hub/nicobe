using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;

namespace NicoBE;

// A slow mock receiver on another UI thread; no real MPC-BE or video is touched.
internal static class MpcControlTest
{
    public static int Run()
    {
        var checks=new List<string>();
        void Check(bool value,string label){if(!value)throw new Exception(label);checks.Add(label);}
        var ready=new TaskCompletionSource<(nint Window,Dispatcher Dispatcher)>(TaskCreationOptions.RunContinuationsAsynchronously);
        int received=0,delay=350;bool callback=false;
        var thread=new Thread(()=>
        {
            try
            {
                using var receiver=new HwndSource(new HwndSourceParameters("NicoBE slow API test") {WindowStyle=0,PositionX=-32000,PositionY=-32000,Width=1,Height=1});
                receiver.AddHook((nint window,int message,nint from,nint pointer,ref bool handled)=>
                {
                    if(message!=Native.WM_COPYDATA)return 0;
                    var data=Marshal.PtrToStructure<Native.CopyData>(pointer);
                    if((uint)data.Code is MpcClient.SetSpeed or MpcClient.JumpSeconds)
                    {
                        Interlocked.Increment(ref received);Thread.Sleep(Volatile.Read(ref delay));
                        if(from!=0)
                        {
                            nint text=Marshal.StringToHGlobalUni("10.0");
                            try
                            {
                                var reply=new Native.CopyData {Code=MpcClient.Position,Bytes=10,Data=text};
                                Native.SendMessageTimeout(from,Native.WM_COPYDATA,window,ref reply,2,1500,out _);
                            }
                            finally{Marshal.FreeHGlobal(text);}
                        }
                    }
                    handled=true;return 1;
                });
                ready.SetResult((receiver.Handle,Dispatcher.CurrentDispatcher));Dispatcher.Run();
            }
            catch(Exception ex){ready.TrySetException(ex);}
        }) {IsBackground=true};
        thread.SetApartmentState(ApartmentState.STA);thread.Start();
        Dispatcher? receiverDispatcher=null;
        T Wait<T>(Task<T> task)
        {
            if(!task.IsCompleted)
            {
                var frame=new DispatcherFrame();
                var timer=new DispatcherTimer {Interval=TimeSpan.FromSeconds(6)};
                timer.Tick+=(_,_)=>frame.Continue=false;
                var dispatcher=Dispatcher.CurrentDispatcher;
                task.ContinueWith(_=>dispatcher.BeginInvoke(()=>frame.Continue=false),TaskScheduler.Default);
                timer.Start();try{Dispatcher.PushFrame(frame);}finally{timer.Stop();}
                if(!task.IsCompleted)throw new TimeoutException("Mock control did not finish");
            }
            return task.GetAwaiter().GetResult();
        }
        try
        {
            nint foreground=Native.GetForegroundWindow();
            var mock=ready.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();receiverDispatcher=mock.Dispatcher;
            using var sender=new HwndSource(new HwndSourceParameters("NicoBE control test sender") {WindowStyle=0,PositionX=-32000,PositionY=-32000,Width=1,Height=1});
            using var client=new MpcClient(sender.Handle,Dispatcher.CurrentDispatcher);
            typeof(MpcClient).GetField("process",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(client,Process.GetCurrentProcess());
            typeof(MpcClient).GetProperty(nameof(MpcClient.PlayerWindow))!.SetValue(client,mock.Window);
            client.Message+=(command,_)=>{if(command==MpcClient.Position)callback=true;};
            nint text=Marshal.StringToHGlobalUni("1.5");
            try
            {
                var data=new Native.CopyData {Code=MpcClient.SetSpeed,Bytes=8,Data=text};
                Check(Native.SendMessageTimeout(mock.Window,Native.WM_COPYDATA,0,ref data,2,150,out _)==0,"legacy 150ms wait reports failure for a slow receiver");
            }
            finally{Marshal.FreeHGlobal(text);}
            var watch=Stopwatch.StartNew();var operation=client.SendControlAsync(MpcClient.SetSpeed,"1.5");
            Check(!operation.IsCompleted && watch.ElapsedMilliseconds<150,"control send returns an incomplete task without blocking the UI");
            bool dispatcherRan=false;Dispatcher.CurrentDispatcher.BeginInvoke(()=>dispatcherRan=true);
            Check(Wait(operation),"slow receiver completes within the dedicated operation timeout");
            Check(dispatcherRan && callback,"dispatcher and synchronous player callback remain responsive while waiting");
            Check(received==2,"one new send is delivered exactly once without retries");
            Check(Native.GetForegroundWindow()==foreground,"sending to a background receiver preserves foreground focus");
            Volatile.Write(ref delay,2200);
            Check(!Wait(client.SendControlAsync(MpcClient.JumpSeconds,"5")),"unresponsive operations still report timeout rather than false success");
            Check(received==3,"relative seek is not retried after timeout");
            var completion=new TaskCompletionSource<bool>();
            using var panel=new PlaybackPanel((_,_)=>completion.Task);
            var change=panel.ChangeSpeedAsync(2);panel.Reset();completion.SetResult(true);
            Check(!Wait(change) && panel.SelectedSpeed==null,"late completion does not restore a speed selection after changing files");
            Paths.WriteJson("mpc-control-test.json",new{passed=true,checks,realPlayerControlled=false});return 0;
        }
        catch(Exception ex){Paths.WriteJson("mpc-control-test.json",new{passed=false,checks,error=ex.ToString()});return 1;}
        finally{receiverDispatcher?.BeginInvokeShutdown(DispatcherPriority.Send);thread.Join(4000);}
    }
}
