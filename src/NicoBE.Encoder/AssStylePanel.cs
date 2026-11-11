using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace NicoBE.Encoder;

internal sealed class AssStylePanel : StackPanel
{
    sealed record FontChoice(string Source,string Label);
    sealed record Choice(int Value,string Label);
    readonly StackPanel fields=new();
    readonly ComboBox font=new(){Height=36,IsTextSearchEnabled=true,MaxDropDownHeight=320};
    readonly ComboBox weight=Select((400,"細い"),(700,"標準"),(900,"太い（対応フォントのみ）"));
    readonly ComboBox outline=Select((0,"なし"),(1,"とても細い"),(2,"細い"),(3,"標準"),(4,"太い"),(5,"とても太い"));
    readonly ComboBox shadow=Select((0,"なし"),(1,"とても小さい"),(2,"小さい"),(3,"標準"),(4,"大きい"),(5,"とても大きい"));
    readonly CheckBox acceleration=new(){Content="オン",FontSize=16};
    readonly Slider size=Slider(50,300,5),speed=Slider(CommentDisplayDefaults.MinScrollSpeedPercent,300,5),spacing=Slider(0,100,5);
    readonly Slider opacity=Slider(10,100,5),fixedTime=Slider(1,30,1),maximum=Slider(1,300,1),advance=Slider(-10,10,.5);
    readonly TextBlock sample=new(){Text="あいうえお ABC 123",FontSize=18,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(12,0,0,0),TextTrimming=TextTrimming.CharacterEllipsis};
    internal AssStylePanel()
    {
        Children.Add(new TextBlock {Text="MKVへのASS格納・外部ASS保存に適用します。",FontSize=14,Foreground=SettingsTheme.Muted,Margin=new Thickness(0,0,0,12)});
        Children.Add(fields);
        var ja=System.Windows.Markup.XmlLanguage.GetLanguage("ja-JP");
        font.ItemsSource=Fonts.SystemFontFamilies.Where(f=>!f.Source.StartsWith('@')&&!f.Source.Contains(','))
            .Select(f=>new FontChoice(f.Source,f.FamilyNames.TryGetValue(ja,out string? name)?name:f.Source))
            .DistinctBy(f=>f.Source,StringComparer.OrdinalIgnoreCase).OrderBy(f=>f.Label,StringComparer.CurrentCulture).ToList();
        font.DisplayMemberPath=nameof(FontChoice.Label);font.SelectedValuePath=nameof(FontChoice.Source);
        var fontRow=new Grid();fontRow.ColumnDefinitions.Add(new());fontRow.ColumnDefinitions.Add(new(){Width=new GridLength(190)});
        fontRow.Children.Add(font);Grid.SetColumn(sample,1);fontRow.Children.Add(sample);
        font.SelectionChanged+=(_,_)=>{if(font.SelectedValue is string value)sample.FontFamily=new FontFamily(value);};
        weight.SelectionChanged+=(_,_)=>{if(weight.SelectedValue is int value)sample.FontWeight=FontWeight.FromOpenTypeWeight(value);};
        acceleration.Checked+=(_,_)=>acceleration.Content="オン";acceleration.Unchecked+=(_,_)=>acceleration.Content="オフ";
        AddRow("長文コメントを加速",acceleration);
        AddRow("フォント",fontRow);AddRow("文字の太さ",weight);AddRow("縁取りの太さ",outline);AddRow("影の大きさ",shadow);
        AddRow("文字サイズ",Number(size,"0' %'"));AddRow("文字速度",Number(speed,"0' %'"));
        AddRow("行間",Number(spacing,"0' %'"));AddRow("時間調整",Number(advance,"+0.#' 秒';-0.#' 秒';0' 秒'"));
        advance.ToolTip="正の値でコメントを早く表示します。";
        AddRow("不透明度",Number(opacity,"0' %'"));AddRow("固定コメントの表示時間",Number(fixedTime,"0' 秒'"));
        AddRow("同時表示数の上限",Number(maximum,"0' 件'"));
        Load(new());
    }
    internal void SetAssEnabled(bool enabled){fields.IsEnabled=enabled;fields.Opacity=enabled?1:.45;}
    internal void Load(AssStyleSettings style)
    {
        style.Validate();
        var choices=((IEnumerable<FontChoice>)font.ItemsSource).ToList();
        if(!choices.Any(c=>c.Source.Equals(style.FontFamily,StringComparison.OrdinalIgnoreCase)))
        {choices.Insert(0,new(style.FontFamily,style.FontFamily));font.ItemsSource=choices;}
        font.SelectedItem=choices.First(c=>c.Source.Equals(style.FontFamily,StringComparison.OrdinalIgnoreCase));
        weight.SelectedValue=style.FontWeight;outline.SelectedValue=style.OutlineLevel;shadow.SelectedValue=style.ShadowLevel;
        size.Value=style.FontSizePercent;speed.Value=style.ScrollSpeedPercent;spacing.Value=style.LineSpacingPercent;
        acceleration.IsChecked=style.AccelerateLongComments;opacity.Value=style.OpacityPercent;fixedTime.Value=style.FixedCommentSeconds;
        maximum.Value=style.MaxVisibleComments;advance.Value=style.AdvanceSeconds;
    }
    internal AssStyleSettings Read()=>new()
    {
        FontFamily=(string)font.SelectedValue,FontWeight=(int)weight.SelectedValue,OutlineLevel=(int)outline.SelectedValue,ShadowLevel=(int)shadow.SelectedValue,
        FontSizePercent=size.Value,ScrollSpeedPercent=speed.Value,LineSpacingPercent=spacing.Value,AccelerateLongComments=acceleration.IsChecked==true,
        OpacityPercent=opacity.Value,FixedCommentSeconds=fixedTime.Value,MaxVisibleComments=(int)maximum.Value,AdvanceSeconds=advance.Value
    };
    void AddRow(string label,FrameworkElement control)
    {
        var row=new Grid();row.ColumnDefinitions.Add(new(){Width=new GridLength(190)});row.ColumnDefinitions.Add(new());
        row.Children.Add(new TextBlock{Text=label,FontSize=16,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,12,0),TextWrapping=TextWrapping.Wrap});
        Grid.SetColumn(control,1);row.Children.Add(control);
        fields.Children.Add(new Border{Child=row,Padding=new Thickness(0,6,0,7),BorderBrush=SettingsTheme.Line,BorderThickness=new Thickness(0,0,0,1)});
    }
    static ComboBox Select(params (int Value,string Label)[] choices)=>new(){Height=36,ItemsSource=choices.Select(c=>new Choice(c.Value,c.Label)).ToArray(),DisplayMemberPath=nameof(Choice.Label),SelectedValuePath=nameof(Choice.Value)};
    static Slider Slider(double min,double max,double step)=>new(){Minimum=min,Maximum=max,TickFrequency=step,IsSnapToTickEnabled=true,SmallChange=step,LargeChange=step*2,MinHeight=36};
    static FrameworkElement Number(Slider slider,string format)
    {
        var row=new DockPanel();var value=new TextBlock{MinWidth=78,FontSize=16,TextAlignment=TextAlignment.Right,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(12,0,0,0)};
        void Update()=>value.Text=slider.Value.ToString(format,CultureInfo.CurrentCulture);
        slider.ValueChanged+=(_,_)=>Update();Update();DockPanel.SetDock(value,Dock.Right);row.Children.Add(value);row.Children.Add(slider);return row;
    }
}
