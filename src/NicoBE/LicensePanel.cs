using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace NicoBE;

public static class LicensePanel
{
    public static string Version=>Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion??"不明";
    public static string ReadResource(string name)
    {
        using var stream=typeof(LicensePanel).Assembly.GetManifestResourceStream("NicoBE."+name)
            ??throw new InvalidOperationException("ライセンス文書がありません。");
        using var reader=new StreamReader(stream);return reader.ReadToEnd();
    }
    public static TabItem Create(string product)
    {
        var panel=new DockPanel {Margin=new Thickness(0,4,6,0)};
        var heading=new TextBlock {Text=product+"  "+Version,FontSize=20,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,0,0,14)};
        DockPanel.SetDock(heading,Dock.Top);panel.Children.Add(heading);
        panel.Children.Add(new TextBox {
            Text="NicoBE / NicoBE Encoder\n\n"+ReadResource("LICENSE")+"\n\n"+ReadResource("THIRD-PARTY-NOTICES"),
            IsReadOnly=true,TextWrapping=TextWrapping.Wrap,FontSize=16,AcceptsReturn=true,
            VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,
            Background=Brushes.Transparent,BorderThickness=new Thickness(0),Padding=new Thickness(0,0,10,10)
        });
        return new TabItem {Header="ライセンス",Content=panel};
    }
}
