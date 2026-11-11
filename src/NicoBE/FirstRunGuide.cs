using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace NicoBE;

// In-window spotlight: no extra native window and no changes to the settings layout.
internal sealed class FirstRunGuide : Canvas
{
    readonly FrameworkElement first,last;
    readonly Path shade=new() {Fill=new SolidColorBrush(Color.FromArgb(185,0,0,0)),IsHitTestVisible=false};
    readonly Border highlight=new() {BorderBrush=SettingsTheme.Accent,BorderThickness=new Thickness(2),CornerRadius=new CornerRadius(6),IsHitTestVisible=false};
    readonly Border card;
    readonly Polygon pointer=new() {Fill=new SolidColorBrush(Color.FromRgb(32,47,54)),Stroke=SettingsTheme.Accent,StrokeThickness=1,IsHitTestVisible=false};
    readonly TextBlock failure=new() {FontSize=14,TextWrapping=TextWrapping.Wrap,Visibility=Visibility.Collapsed,Margin=new Thickness(0,10,0,0)};
    internal Button Accept {get;}=new() {Content="わかりました",FontSize=16,MinWidth=140,Padding=new Thickness(18,8,18,8),HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,16,0,0),IsDefault=true};
    Rect previous=Rect.Empty;Size previousSize;
    internal FirstRunGuide(FrameworkElement first,FrameworkElement last,Action acknowledge,
        string title="コメントの取得方法",string message="NicoJKを使っている方は、ログフォルダを指定してください。\nログがない方は、自動取得をオンのままお使いください。")
    {
        this.first=first;this.last=last;Background=Brushes.Transparent;ClipToBounds=true;
        KeyboardNavigation.SetTabNavigation(this,KeyboardNavigationMode.Cycle);
        KeyboardNavigation.SetControlTabNavigation(this,KeyboardNavigationMode.Cycle);
        var body=new StackPanel();body.Children.Add(new TextBlock {Text=title,FontSize=20,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,0,0,12)});
        body.Children.Add(new TextBlock {Text=message,FontSize=16,TextWrapping=TextWrapping.Wrap,LineHeight=26});
        Accept.SetResourceReference(StyleProperty,"PrimaryButton");body.Children.Add(Accept);
        failure.SetResourceReference(TextBlock.ForegroundProperty,"ErrorBrush");body.Children.Add(failure);
        card=new Border {Child=body,Padding=new Thickness(20),CornerRadius=new CornerRadius(6),BorderBrush=SettingsTheme.Accent,BorderThickness=new Thickness(1),Background=new SolidColorBrush(Color.FromRgb(32,47,54))};
        Children.Add(shade);Children.Add(highlight);Children.Add(card);Children.Add(pointer);
        Accept.Click+=(_,_)=>{try{acknowledge();}catch(Exception ex){failure.Text="案内の確認状態を保存できませんでした。\n"+ex.Message;failure.Visibility=Visibility.Visible;previous=Rect.Empty;UpdatePlacement();}};
        LayoutUpdated+=OnLayout;Unloaded+=(_,_)=>Detach();
    }
    void OnLayout(object? sender,EventArgs args)=>UpdatePlacement();
    internal void Detach()=>LayoutUpdated-=OnLayout;
    void UpdatePlacement()
    {
        if(ActualWidth<=0 || ActualHeight<=0 || !IsAncestorOf(card))return;
        var top=first.TranslatePoint(new Point(),this);var bottom=last.TranslatePoint(new Point(last.ActualWidth,last.ActualHeight),this);
        var hole=new Rect(new Point(Math.Max(6,top.X-6),Math.Max(6,top.Y-4)),new Point(Math.Min(ActualWidth-6,bottom.X+6),Math.Min(ActualHeight-6,bottom.Y+4)));
        var size=new Size(ActualWidth,ActualHeight);if(hole==previous && size==previousSize)return;previous=hole;previousSize=size;
        var mask=new GeometryGroup {FillRule=FillRule.EvenOdd};mask.Children.Add(new RectangleGeometry(new Rect(size)));mask.Children.Add(new RectangleGeometry(hole,6,6));shade.Data=mask;
        SetLeft(highlight,hole.Left);SetTop(highlight,hole.Top);highlight.Width=hole.Width;highlight.Height=hole.Height;
        card.Width=Math.Min(660,ActualWidth-48);card.Measure(new Size(card.Width,double.PositiveInfinity));
        bool above=hole.Bottom+24+card.DesiredSize.Height>ActualHeight-20 && hole.Top-card.DesiredSize.Height-24>=20;
        double cardTop=above?hole.Top-card.DesiredSize.Height-24:Math.Max(hole.Bottom+16,Math.Min(hole.Bottom+24,ActualHeight-card.DesiredSize.Height-20));
        SetLeft(card,(ActualWidth-card.Width)/2);SetTop(card,cardTop);
        double edge=above?cardTop+card.DesiredSize.Height-.5:cardTop+.5;
        pointer.Points=new PointCollection {new(ActualWidth/2-10,edge),new(ActualWidth/2,edge+(above?11:-11)),new(ActualWidth/2+10,edge)};
    }
}
