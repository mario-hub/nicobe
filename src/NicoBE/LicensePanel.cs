using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace NicoBE;

public static class LicensePanel
{
    public static string Version=>Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion??"不明";
    public static string ReleaseDate=>Assembly.GetEntryAssembly()?.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a=>a.Key=="ReleaseDate")?.Value??"不明";
    public const string WebsiteUrl="https://www.moreread.net/apps/nicobe/index.html";
    internal static ProcessStartInfo WebsiteStartInfo()=>new(WebsiteUrl) {UseShellExecute=true};
    public static string ReadResource(string name)
    {
        using var stream=typeof(LicensePanel).Assembly.GetManifestResourceStream("NicoBE."+name)
            ??throw new InvalidOperationException("ライセンス文書がありません。");
        using var reader=new StreamReader(stream);return reader.ReadToEnd();
    }
    public static TabItem Create(string product)
    {
        var panel=new DockPanel {Margin=new Thickness(0,4,6,0)};
        var info=new StackPanel {Margin=new Thickness(0,0,0,16)};
        info.Children.Add(new TextBlock {Text=product,FontSize=20,FontWeight=FontWeights.SemiBold});
        info.Children.Add(new TextBlock {Name="ApplicationVersionInfo",Text=$"バージョン {Version}　／　公開日 {ReleaseDate}",FontSize=16,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,6,0,0)});
        var website=new Button {Content="Webサイトを表示",FontSize=16,HorizontalAlignment=HorizontalAlignment.Left,Margin=new Thickness(0,12,0,0),ToolTip=WebsiteUrl};
        website.Click+=(_,_)=>
        {
            try {using var process=Process.Start(WebsiteStartInfo());}
            catch(Exception ex){MessageBox.Show("Webサイトを開けませんでした。\n"+ex.Message,product,MessageBoxButton.OK,MessageBoxImage.Error);}
        };
        info.Children.Add(website);
        DockPanel.SetDock(info,Dock.Top);panel.Children.Add(info);
        var label=new Border {BorderBrush=SettingsTheme.Line,BorderThickness=new Thickness(0,1,0,0),Padding=new Thickness(0,14,0,10),
            Child=new TextBlock {Text="ライセンス",FontSize=16,FontWeight=FontWeights.SemiBold}};
        DockPanel.SetDock(label,Dock.Top);panel.Children.Add(label);
        panel.Children.Add(new TextBox {
            Text="NicoBE / NicoBE Encoder\n\n"+ReadResource("LICENSE")+"\n\n"+ReadResource("THIRD-PARTY-NOTICES"),
            IsReadOnly=true,TextWrapping=TextWrapping.Wrap,FontSize=16,AcceptsReturn=true,
            VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,
            Background=Brushes.Transparent,BorderThickness=new Thickness(0),Padding=new Thickness(0,0,10,10)
        });
        return new TabItem {Header="その他",Content=panel};
    }
}
