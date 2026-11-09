using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace NicoBE;

public sealed class NgSettingsPanel : UserControl
{
    readonly Func<NgLogSnapshot?> readLog;
    readonly TextBox ids=Editor(),words=Editor();
    readonly TextBox word=new() {FontSize=16,Height=36,Padding=new Thickness(6,4,6,4),MinWidth=100};
    readonly TextBlock context=Note(""),feedback=Note("");
    readonly TextBlock heading=new() {Text="再生位置 ／ 前後5秒",VerticalAlignment=VerticalAlignment.Center,FontWeight=FontWeights.SemiBold};
    readonly DataGrid rows=new()
    {
        AutoGenerateColumns=false,IsReadOnly=true,CanUserAddRows=false,CanUserDeleteRows=false,
        SelectionMode=DataGridSelectionMode.Single,SelectionUnit=DataGridSelectionUnit.FullRow,
        HeadersVisibility=DataGridHeadersVisibility.Column,RowHeaderWidth=0,FontSize=16,MinRowHeight=28,
        Height=180,EnableRowVirtualization=true,EnableColumnVirtualization=false,
        GridLinesVisibility=DataGridGridLinesVisibility.Horizontal,Margin=new Thickness(0,8,0,10)
    };
    readonly Button addId;
    public NgSettingsPanel(Config config,Func<NgLogSnapshot?> readLog)
    {
        this.readLog=readLog;
        ids.Text=string.Join(Environment.NewLine,config.NgIds);words.Text=string.Join(Environment.NewLine,config.NgWords);
        var body=new StackPanel {Margin=new Thickness(0,4,6,4)};
        Content=new ScrollViewer {Content=body,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};
        var header=new DockPanel();var refresh=Button("更新",Capture);DockPanel.SetDock(refresh,Dock.Right);header.Children.Add(refresh);
        header.Children.Add(heading);body.Children.Add(header);
        context.TextWrapping=TextWrapping.NoWrap;context.TextTrimming=TextTrimming.CharacterEllipsis;body.Children.Add(context);
        rows.Columns.Add(new DataGridTextColumn {Header="時刻",Binding=new Binding(nameof(NgLogRow.Time)),Width=126});
        rows.Columns.Add(new DataGridTextColumn {Header="ID",Binding=new Binding(nameof(NgLogRow.UserId)),Width=145});
        rows.Columns.Add(new DataGridTextColumn {Header="コメント",Binding=new Binding(nameof(NgLogRow.Text)),Width=new DataGridLength(1,DataGridLengthUnitType.Star)});
        body.Children.Add(rows);
        var candidate=new DockPanel();addId=Button("IDをNGに追加",()=>
        {
            if(rows.SelectedItem is NgLogRow row && row.UserId.Length>0)Add(ids,row.UserId,StringComparer.Ordinal,"NG ID");
        });addId.IsEnabled=false;addId.Margin=new Thickness(0,0,12,0);DockPanel.SetDock(addId,Dock.Left);candidate.Children.Add(addId);
        var addWord=Button("ワードを追加",()=>Add(words,word.Text,StringComparer.OrdinalIgnoreCase,"NGワード"));DockPanel.SetDock(addWord,Dock.Right);candidate.Children.Add(addWord);candidate.Children.Add(word);body.Children.Add(candidate);
        word.ToolTip="選択したコメントを編集し、NGにしたい部分だけを残して追加できます。直接入力もできます。";
        body.Children.Add(Note("コメントを選択し、NGにしたい部分を入力欄で編集して追加できます。"));
        rows.SelectionChanged+=(_,_)=>
        {
            var row=rows.SelectedItem as NgLogRow;word.Text=row?.Text??"";addId.IsEnabled=!string.IsNullOrEmpty(row?.UserId);
        };
        var editors=new Grid {Margin=new Thickness(0,12,0,0)};
        editors.ColumnDefinitions.Add(new ColumnDefinition());editors.ColumnDefinitions.Add(new ColumnDefinition());
        var idField=Field("NG ID（完全一致）",ids);idField.Margin=new Thickness(0,0,10,0);editors.Children.Add(idField);
        var wordField=Field("NGワード（部分一致）",words);wordField.Margin=new Thickness(10,0,0,0);Grid.SetColumn(wordField,1);editors.Children.Add(wordField);body.Children.Add(editors);
        body.Children.Add(Note("各欄に1行1件で直接入力できます。解除するには行を削除してください。\nNGワードは英字の大小を区別しません。保存すると全プレイヤーに反映します。"));
        feedback.Foreground=SettingsTheme.Accent;body.Children.Add(feedback);
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
    }
    void Add(TextBox target,string value,StringComparer comparer,string kind)
    {
        if(string.IsNullOrWhiteSpace(value)){feedback.Text="追加する文字を入力してください。";return;}
        var entries=Entries(target,comparer);int before=entries.Count;
        entries=NgFilter.Normalize(entries.Concat(value.Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries)),comparer);
        target.Text=string.Join(Environment.NewLine,entries);target.ScrollToEnd();
        feedback.Text=entries.Count==before?"すでに登録されています。":$"{kind}を追加しました。「保存して反映」で適用します。";
    }
    static List<string> Entries(TextBox box,StringComparer comparer)=>NgFilter.Normalize(box.Text.Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries),comparer);
    static TextBox Editor()=>new() {AcceptsReturn=true,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,Height=110,FontSize=16,Padding=new Thickness(8),TextWrapping=TextWrapping.NoWrap,VerticalContentAlignment=VerticalAlignment.Top};
    static FrameworkElement Field(string label,TextBox box)
    {
        var panel=new StackPanel();panel.Children.Add(new TextBlock {Text=label,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,0,0,4)});panel.Children.Add(box);return panel;
    }
    static TextBlock Note(string text)=>new() {Text=text,FontSize=14,TextWrapping=TextWrapping.Wrap,Foreground=SettingsTheme.Muted,Margin=new Thickness(0,6,0,0),VerticalAlignment=VerticalAlignment.Center};
    static Button Button(string label,Action action)
    {
        var button=new Button {Content=label,FontSize=16,Padding=new Thickness(10,4,10,4),Margin=new Thickness(6,0,0,0)};button.Click+=(_,_)=>action();return button;
    }
}
