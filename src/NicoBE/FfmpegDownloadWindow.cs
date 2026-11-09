using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace NicoBE;

public sealed class FfmpegDownloadWindow : Window
{
    readonly CancellationTokenSource cancellation=new();
    readonly TextBlock message=new() {Text="ダウンロードを準備しています…",TextWrapping=TextWrapping.Wrap};
    readonly ProgressBar progress=new() {Height=8,IsIndeterminate=true,Minimum=0,Maximum=1,Margin=new Thickness(0,18,0,18),Foreground=SettingsTheme.Accent};
    readonly Button cancel=new() {Content="中止",MinWidth=100,HorizontalAlignment=HorizontalAlignment.Right};
    bool finished,started;
    public string? InstalledPath {get;private set;}
    public FfmpegDownloadWindow()
    {
        Title="FFmpegのダウンロード";Width=540;SizeToContent=SizeToContent.Height;ResizeMode=ResizeMode.NoResize;WindowStartupLocation=WindowStartupLocation.CenterOwner;ShowInTaskbar=false;
        FontFamily=new FontFamily("Yu Gothic UI");FontSize=16;SettingsTheme.Apply(this);
        var panel=new StackPanel {Margin=new Thickness(24)};Content=panel;
        panel.Children.Add(new TextBlock {Text="FFmpegをセットアップ",FontSize=20,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,0,0,18)});
        panel.Children.Add(message);panel.Children.Add(progress);panel.Children.Add(cancel);
        cancel.Click+=(_,_)=>{if(finished)Close();else Cancel();};
        Closing+=(_,e)=>{if(started&&!finished){e.Cancel=true;Cancel();}};
        Closed+=(_,_)=>cancellation.Dispose();
        Loaded+=async (_,_)=>
        {
            if(started)return;started=true;
            try
            {
                using var client=new HttpClient {Timeout=TimeSpan.FromMinutes(20)};
                var reporter=new Progress<InstallProgress>(p=>{if(finished||cancellation.IsCancellationRequested)return;message.Text=p.Message;progress.IsIndeterminate=!p.Fraction.HasValue;if(p.Fraction.HasValue)progress.Value=p.Fraction.Value;});
                InstalledPath=await FfmpegInstaller.Install(client,FfmpegInstaller.InstallRoot,reporter,cancellation.Token);
                finished=true;DialogResult=true;
            }
            catch(OperationCanceledException)
            {
                finished=true;
                if(cancellation.IsCancellationRequested)DialogResult=false;
                else Failure("接続がタイムアウトしました。もう一度お試しください。");
            }
            catch(Exception ex){finished=true;Failure(ex.Message);}
        };
    }
    void Cancel(){cancellation.Cancel();cancel.IsEnabled=false;message.Text="中止しています…";}
    void Failure(string text)
    {
        message.Text="ダウンロードに失敗しました。\n\n"+text;message.SetResourceReference(TextBlock.ForegroundProperty,"ErrorBrush");progress.Visibility=Visibility.Collapsed;cancel.Content="閉じる";cancel.IsEnabled=true;
    }
}
