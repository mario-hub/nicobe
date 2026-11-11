namespace NicoBE;

// ASS-capable settings only. This is also the Encoder JSON's AssStyle object.
public sealed class AssStyleSettings
{
    public string FontFamily {get;set;} = CommentDisplayDefaults.FontFamily;
    public int FontWeight {get;set;} = CommentDisplayDefaults.FontWeight;
    public int OutlineLevel {get;set;} = CommentDisplayDefaults.OutlineLevel;
    public int ShadowLevel {get;set;} = CommentDisplayDefaults.ShadowLevel;
    public double FontSizePercent {get;set;} = CommentDisplayDefaults.FontSizePercent;
    public double ScrollSpeedPercent {get;set;} = CommentDisplayDefaults.ScrollSpeedPercent;
    public double LineSpacingPercent {get;set;} = CommentDisplayDefaults.LineSpacingPercent;
    public bool AccelerateLongComments {get;set;} = CommentDisplayDefaults.AccelerateLongComments;
    public double OpacityPercent {get;set;} = CommentDisplayDefaults.Opacity*100;
    public double FixedCommentSeconds {get;set;} = CommentDisplayDefaults.FixedCommentSeconds;
    public int MaxVisibleComments {get;set;} = CommentDisplayDefaults.MaxVisibleComments;
    public double AdvanceSeconds {get;set;} = CommentDisplayDefaults.AdvanceSeconds;

    public void Validate()
    {
        static bool Range(double value,double min,double max)=>double.IsFinite(value)&&value>=min&&value<=max;
        if(string.IsNullOrWhiteSpace(FontFamily)||FontFamily.Length>200||FontFamily.IndexOfAny(new[]{',','\r','\n','{','}'})>=0)
            throw new ArgumentException("ASSのフォント名が不正です。");
        if(FontWeight is not (400 or 700 or 900)||OutlineLevel is <0 or >5||ShadowLevel is <0 or >5
            ||!Range(FontSizePercent,50,300)||!Range(ScrollSpeedPercent,CommentDisplayDefaults.MinScrollSpeedPercent,300)
            ||!Range(LineSpacingPercent,0,100)||!Range(OpacityPercent,10,100)||!Range(FixedCommentSeconds,1,30)
            ||MaxVisibleComments is <1 or >300||!Range(AdvanceSeconds,-10,10))
            throw new ArgumentException("ASSの表示設定が範囲外です。");
    }
}
