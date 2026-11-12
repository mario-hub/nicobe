using System.Windows;
using System.Windows.Controls;

namespace NicoBE.Encoder;

internal sealed class ExtraVideoArgumentsPanel : StackPanel
{
    internal readonly CheckBox EnabledSwitch=new() {Content="追加パラメータを使う"};
    internal readonly TextBox Input=new() {AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,MinHeight=96,MaxHeight=180,FontSize=16,Margin=new Thickness(0,8,0,8)};
    internal string? LastCommand {get;set;}
    internal ExtraVideoArgumentsPanel(Func<EncoderSettings> readSettings,Window owner)
    {
        Margin=new Thickness(0,0,0,14);
        var panel=this;
        panel.Children.Add(EnabledSwitch);
        panel.Children.Add(new TextBlock {Text="映像エンコードの詳細オプションを指定します。同じ項目はこちらを優先します。",TextWrapping=TextWrapping.Wrap,FontSize=14,Foreground=SettingsTheme.Muted,Margin=new Thickness(0,8,0,0)});
        panel.Children.Add(Input);
        var example=new TextBlock {Text="例：-maxrate:v 6M -bufsize:v 12M -bf:v 3 -g:v 120",FontSize=14,Foreground=SettingsTheme.Muted,TextWrapping=TextWrapping.Wrap};panel.Children.Add(example);
        var actions=new StackPanel {Orientation=Orientation.Horizontal,Margin=new Thickness(0,10,0,0)};panel.Children.Add(actions);
        var preview=new Button {Content="引数を表示",Margin=new Thickness(0,0,8,0)};actions.Children.Add(preview);
        preview.Click+=(_,_)=>
        {
            try
            {
                var settings=readSettings();settings.Validate(requireFiles:false);
                bool two=settings.Backend=="CPU" && settings.Passes==2;
                var commands=new List<string>();
                foreach(int pass in two?new[]{1,2}:new[]{0})
                    commands.Add(ExtraVideoArguments.Command(settings.Ffmpeg,EncoderJob.Arguments(settings,"入力.ts","出力."+settings.Container.ToLowerInvariant(),0,new[]{1},null,pass,"パス統計")));
                Show("現在の設定の引数（入力・出力名とトラック番号は例です。実際の引数は処理ログに保存します。）\n\n"+string.Join("\n\n",commands)
                    +(LastCommand==null?"":"\n\n直近のエンコードで実際に渡した引数\n\n"+LastCommand));
            }
            catch(Exception ex){MessageBox.Show(owner,ex.Message,"追加パラメータ",MessageBoxButton.OK,MessageBoxImage.Warning);}
        };
        var help=new Button {Content="指定できる項目"};actions.Children.Add(help);help.Click+=(_,_)=>Show(ExtraVideoArguments.Help);
        EnabledSwitch.Checked+=(_,_)=>Update();EnabledSwitch.Unchecked+=(_,_)=>Update();Update();
        void Show(string text)
        {
            var dialog=new Window {Title="FFmpeg追加パラメータ",Owner=owner,Width=780,Height=480,MinWidth=560,MinHeight=320,FontSize=16,FontFamily=owner.FontFamily,WindowStartupLocation=WindowStartupLocation.CenterOwner};SettingsTheme.Apply(dialog);
            var root=new DockPanel {Margin=new Thickness(16)};dialog.Content=root;
            var buttons=new StackPanel {Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,12,0,0)};DockPanel.SetDock(buttons,Dock.Bottom);root.Children.Add(buttons);
            var copy=new Button {Content="コピー",Margin=new Thickness(0,0,8,0)};buttons.Children.Add(copy);copy.Click+=(_,_)=>{try{Clipboard.SetText(text);copy.Content="コピーしました";}catch(Exception ex){MessageBox.Show(dialog,ex.Message,"コピーできませんでした");}};
            var close=new Button {Content="閉じる",IsCancel=true};buttons.Children.Add(close);close.Click+=(_,_)=>dialog.Close();
            root.Children.Add(new TextBox {Text=text,IsReadOnly=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto});dialog.ShowDialog();
        }
    }
    void Update(){Input.IsEnabled=EnabledSwitch.IsChecked==true;Input.Opacity=Input.IsEnabled?1:.5;}
    internal void Load(EncoderSettings settings){EnabledSwitch.IsChecked=settings.ExtraVideoArgumentsEnabled;Input.Text=settings.ExtraVideoArguments;}
    internal void Store(EncoderSettings settings){settings.ExtraVideoArgumentsEnabled=EnabledSwitch.IsChecked==true;settings.ExtraVideoArguments=Input.Text;}
}
