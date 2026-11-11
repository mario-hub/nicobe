using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace NicoBE;

public sealed class NgSettingsPanel : UserControl
{
    readonly Func<NgLogSnapshot?> readLog;
    readonly TextBox ids=Editor(),words=Editor();
    readonly CheckBox colored=new(),fixedPosition=new(),sizeChanged=new(),multiline=new();
    readonly TextBox word=new() {FontSize=16,Height=36,Padding=new Thickness(6,4,6,4),MinWidth=100};
    readonly TextBlock context=Note(""),feedback=Note("");
    readonly TextBlock heading=new() {Text="再生位置 ／ 前後5秒",VerticalAlignment=VerticalAlignment.Center,FontWeight=FontWeights.SemiBold};
    readonly DataGrid rows=new()
    {
        AutoGenerateColumns=false,IsReadOnly=true,CanUserAddRows=false,CanUserDeleteRows=false,
        SelectionMode=DataGridSelectionMode.Single,SelectionUnit=DataGridSelectionUnit.FullRow,
        HeadersVisibility=DataGridHeadersVisibility.Column,RowHeaderWidth=0,FontSize=16,MinRowHeight=32,
        Height=240,EnableRowVirtualization=true,EnableColumnVirtualization=false,
        GridLinesVisibility=DataGridGridLinesVisibility.Horizontal,Margin=new Thickness(0,12,0,12)
    };
    readonly Button addId;
    public NgSettingsPanel(Config config,Func<NgLogSnapshot?> readLog)
    {
        this.readLog=readLog;
        ids.Text=string.Join(Environment.NewLine,config.NgIds);words.Text=string.Join(Environment.NewLine,config.NgWords);
        var body=new StackPanel {Margin=new Thickness(0,6,12,16)};
        Content=new ScrollViewer {Content=body,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};
        var filters=new StackPanel();filters.Children.Add(SectionHeading("種類で非表示"));
        var switches=new Grid {Margin=new Thickness(0,6,0,0)};
        switches.ColumnDefinitions.Add(new ColumnDefinition());switches.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(24)});switches.ColumnDefinitions.Add(new ColumnDefinition());
        switches.RowDefinitions.Add(new RowDefinition());switches.RowDefinitions.Add(new RowDefinition());
        var choices=new[]{Toggle("色コメントNG",colored,config.NgColoredComments,"白以外のコメントを非表示にします。"),
            Toggle("固定コメントNG",fixedPosition,config.NgFixedComments,"上・下固定コメントを非表示にします。"),
            Toggle("サイズ変更コメントNG",sizeChanged,config.NgSizeChangedComments,"標準サイズ以外のコメントを非表示にします。"),
            Toggle("複数行コメントNG",multiline,config.NgMultilineComments,"改行を含むコメントを非表示にします。")};
        for(int i=0;i<choices.Length;i++){Grid.SetRow(choices[i],i/2);Grid.SetColumn(choices[i],i%2*2);switches.Children.Add(choices[i]);}
        filters.Children.Add(switches);
        var filterBox=new Border {Child=filters,Padding=new Thickness(16,12,16,12),CornerRadius=new CornerRadius(5),BorderBrush=SettingsTheme.Line,BorderThickness=new Thickness(1),Margin=new Thickness(0,0,0,24)};
        filterBox.SetResourceReference(BackgroundProperty,"SurfaceBrush");body.Children.Add(filterBox);
        body.Children.Add(SectionHeading("再生中のコメント"));
        var header=new DockPanel {Margin=new Thickness(0,10,0,0)};var refresh=Button("更新",Capture);DockPanel.SetDock(refresh,Dock.Right);header.Children.Add(refresh);
        header.Children.Add(heading);body.Children.Add(header);
        context.TextWrapping=TextWrapping.NoWrap;context.TextTrimming=TextTrimming.CharacterEllipsis;body.Children.Add(context);
        rows.Columns.Add(new DataGridTextColumn {Header="時刻",Binding=new Binding(nameof(NgLogRow.Time)),Width=126});
        rows.Columns.Add(new DataGridTextColumn {Header="ID",Binding=new Binding(nameof(NgLogRow.UserId)),Width=145});
        rows.Columns.Add(new DataGridTextColumn {Header="コメント",Binding=new Binding(nameof(NgLogRow.Text)),Width=new DataGridLength(1,DataGridLengthUnitType.Star)});
        body.Children.Add(rows);
        var candidate=new DockPanel();addId=Button("選択したIDをNGに追加",()=>
        {
            if(rows.SelectedItem is NgLogRow row && row.UserId.Length>0)Add(ids,row.UserId,StringComparer.Ordinal,"NG ID");
        });addId.IsEnabled=false;addId.Margin=new Thickness(0);addId.HorizontalAlignment=HorizontalAlignment.Left;body.Children.Add(addId);
        var wordLabel=Note("NGにするワード（選択したコメントを編集できます）");wordLabel.Margin=new Thickness(0,14,0,6);body.Children.Add(wordLabel);
        var addWord=Button("ワードを追加",()=>Add(words,word.Text,StringComparer.OrdinalIgnoreCase,"NGワード"));addWord.Margin=new Thickness(10,0,0,0);DockPanel.SetDock(addWord,Dock.Right);candidate.Children.Add(addWord);candidate.Children.Add(word);body.Children.Add(candidate);
        word.ToolTip="選択したコメントを編集し、NGにしたい部分だけを残して追加できます。直接入力もできます。";
        feedback.Foreground=SettingsTheme.Accent;feedback.Margin=new Thickness(0,8,0,0);body.Children.Add(feedback);
        rows.SelectionChanged+=(_,_)=>
        {
            var row=rows.SelectedItem as NgLogRow;word.Text=row?.Text??"";addId.IsEnabled=!string.IsNullOrEmpty(row?.UserId);
        };
        var savedHeading=SectionHeading("登録済みのNG");savedHeading.Margin=new Thickness(0,24,0,0);body.Children.Add(savedHeading);
        body.Children.Add(Note("1行1件で直接編集できます。解除するには行を削除してください。"));
        var editors=new Grid {Margin=new Thickness(0,14,0,0)};
        editors.ColumnDefinitions.Add(new ColumnDefinition());editors.ColumnDefinitions.Add(new ColumnDefinition());
        var idField=Field("NG ID（完全一致）",ids);idField.Margin=new Thickness(0,0,10,0);editors.Children.Add(idField);
        var wordField=Field("NGワード（部分一致）",words);wordField.Margin=new Thickness(10,0,0,0);Grid.SetColumn(wordField,1);editors.Children.Add(wordField);body.Children.Add(editors);
        body.Children.Add(Note("NGワードは英字の大小を区別しません。「反映」で全プレイヤーに適用します。"));
    }
    public void Capture()
    {
        var snapshot=readLog();rows.ItemsSource=null;word.Clear();addId.IsEnabled=false;feedback.Text="";heading.Text="再生位置 ／ 前後5秒";
        if(snapshot==null){context.Text="連携中のMPC-BEがありません。手動入力は利用できます。";return;}
        if(!snapshot.Ready){context.Text=$"{snapshot.Title}\nコメント未準備です。準備後に「更新」を押してください。";return;}
        rows.ItemsSource=snapshot.Rows;
        string time=new NgLogRow(snapshot.Position,"","").Time;
        heading.Text=$"再生位置 {time} ／ 前後5秒";
        context.Text=$"{snapshot.Title}　·　{snapshot.Rows.Count}件（NG対象も含む）";context.ToolTip=context.Text;
    }
    public void SaveTo(Config config)
    {
        config.NgIds=Entries(ids,StringComparer.Ordinal);config.NgWords=Entries(words,StringComparer.OrdinalIgnoreCase);
        config.NgColoredComments=colored.IsChecked==true;config.NgFixedComments=fixedPosition.IsChecked==true;
        config.NgSizeChangedComments=sizeChanged.IsChecked==true;config.NgMultilineComments=multiline.IsChecked==true;
    }
    void Add(TextBox target,string value,StringComparer comparer,string kind)
    {
        if(string.IsNullOrWhiteSpace(value)){feedback.Text="追加する文字を入力してください。";return;}
        var entries=Entries(target,comparer);int before=entries.Count;
        entries=NgFilter.Normalize(entries.Concat(value.Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries)),comparer);
        target.Text=string.Join(Environment.NewLine,entries);target.ScrollToEnd();
        feedback.Text=entries.Count==before?"すでに登録されています。":$"{kind}を追加しました。「反映」で適用します。";
    }
    static FrameworkElement Toggle(string label,CheckBox toggle,bool enabled,string hint)
    {
        toggle.FontSize=16;toggle.ToolTip=hint;toggle.IsChecked=enabled;toggle.Content=enabled?"オン":"オフ";
        toggle.Checked+=(_,_)=>toggle.Content="オン";toggle.Unchecked+=(_,_)=>toggle.Content="オフ";
        var row=new Grid {MinHeight=44};
        row.ColumnDefinitions.Add(new ColumnDefinition());row.ColumnDefinitions.Add(new ColumnDefinition {Width=GridLength.Auto});
        toggle.Margin=new Thickness(12,0,0,0);toggle.VerticalAlignment=VerticalAlignment.Center;
        row.Children.Add(new TextBlock {Text=label,FontSize=16,FontWeight=FontWeights.SemiBold,VerticalAlignment=VerticalAlignment.Center});
        Grid.SetColumn(toggle,1);row.Children.Add(toggle);return row;
    }
    static List<string> Entries(TextBox box,StringComparer comparer)=>NgFilter.Normalize(box.Text.Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries),comparer);
    static TextBlock SectionHeading(string text)=>new() {Text=text,FontSize=16,FontWeight=FontWeights.SemiBold};
    static TextBox Editor()=>new() {AcceptsReturn=true,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,Height=140,FontSize=16,Padding=new Thickness(8),TextWrapping=TextWrapping.NoWrap,VerticalContentAlignment=VerticalAlignment.Top};
    static FrameworkElement Field(string label,TextBox box)
    {
        var panel=new StackPanel();panel.Children.Add(new TextBlock {Text=label,FontSize=16,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,0,0,8)});panel.Children.Add(box);return panel;
    }
    static TextBlock Note(string text)=>new() {Text=text,FontSize=14,TextWrapping=TextWrapping.Wrap,Foreground=SettingsTheme.Muted,Margin=new Thickness(0,6,0,0),VerticalAlignment=VerticalAlignment.Center};
    static Button Button(string label,Action action)
    {
        var button=new Button {Content=label,FontSize=16,Padding=new Thickness(10,4,10,4),Margin=new Thickness(6,0,0,0)};button.Click+=(_,_)=>action();return button;
    }
}
