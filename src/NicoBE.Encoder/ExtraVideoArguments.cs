using System.Text;
using System.Text.RegularExpressions;

namespace NicoBE.Encoder;

// Deliberately limited to encoding controls: no files, filters, stream mapping,
// timestamps or pass/statistics management may be supplied through this field.
internal static class ExtraVideoArguments
{
    static readonly HashSet<string> Common=new(StringComparer.Ordinal) {"b","maxrate","bufsize","g","bf","refs","qmin","qmax","threads"};
    static readonly HashSet<string> X264Keys=new(StringComparer.Ordinal) {"aq-mode","aq-strength","psy-rd","rc-lookahead","bframes","b-adapt","b-pyramid","keyint","min-keyint","scenecut","ref","deblock","me","subme","trellis"};
    static readonly HashSet<string> X265Keys=new(X264Keys,StringComparer.Ordinal) {"psy-rdoq","rd","rdoq-level","sao","strong-intra-smoothing","pools","frame-threads"};
    internal const string Help="共通: -b:v / -maxrate:v / -bufsize:v / -g:v / -bf:v / -refs:v / -qmin:v / -qmax:v / -threads:v\nCPU H.264・H.265: -preset / -tune / -crf / -x264-params または -x265-params\nCPU AV1: -cpu-used / -row-mt / -crf / -lag-in-frames / -aq-mode\nNVENC: -preset / -tune / -rc / -cq / -rc-lookahead / -spatial-aq / -temporal-aq / -aq-strength / -b_ref_mode\nQSV: -preset / -global_quality / -look_ahead / -look_ahead_depth\nAMF: -quality / -rc / -qp_i / -qp_p / -qp_b\n\n-x264-params / -x265-params は画質・GOPなどの対応キーのみ指定できます。\n実際に使える値はFFmpegとエンコーダーに依存します。入力・出力・音声・フィルター・パス数の指定はできません。";

    internal static bool Active(EncoderSettings s)=>s.ExtraVideoArgumentsEnabled && !string.IsNullOrWhiteSpace(s.ExtraVideoArguments);

    internal static List<string> Tokenize(string text)
    {
        if(text.Length>16384)throw new Exception("追加パラメータが長すぎます（16,384文字まで）。");
        var tokens=new List<string>();var value=new StringBuilder();char quote='\0';bool started=false;
        for(int i=0;i<text.Length;i++)
        {
            char c=text[i];
            if(c=='\\' && i+1<text.Length && (text[i+1]=='"' || text[i+1]=='\'')) {value.Append(text[++i]);started=true;continue;}
            if(quote!='\0') {if(c==quote)quote='\0';else value.Append(c);started=true;continue;}
            if(c is '"' or '\''){quote=c;started=true;continue;}
            if(char.IsWhiteSpace(c)){if(started){tokens.Add(value.ToString());value.Clear();started=false;}continue;}
            value.Append(c);started=true;
        }
        if(quote!='\0')throw new Exception("追加パラメータの引用符が閉じていません。");
        if(started)tokens.Add(value.ToString());return tokens;
    }
    static string Key(string option)=>option.EndsWith(":v",StringComparison.Ordinal)?option[1..^2]:option.StartsWith('-')?option[1..]:"";
    static bool Supports(EncoderSettings s,string key)=>Common.Contains(key) || s.Backend switch
    {
        "CPU" when s.Codec=="AV1"=>key is "cpu-used" or "row-mt" or "crf" or "lag-in-frames" or "aq-mode",
        "CPU"=>key is "preset" or "tune" or "crf" || key==(s.Codec=="H265"?"x265-params":"x264-params"),
        "NVENC"=>key is "preset" or "tune" or "rc" or "cq" or "rc-lookahead" or "spatial-aq" or "temporal-aq" or "aq-strength" or "b_ref_mode",
        "QSV"=>key is "preset" or "global_quality" or "look_ahead" or "look_ahead_depth",
        "AMF"=>key is "quality" or "rc" or "qp_i" or "qp_p" or "qp_b",
        _=>false
    };
    static Dictionary<string,string> Compound(string key,string value)
    {
        var allowed=key=="x265-params"?X265Keys:X264Keys;var result=new Dictionary<string,string>(StringComparer.Ordinal);
        foreach(string part in value.Split(':'))
        {
            int split=part.IndexOf('=');
            if(split<=0 || !allowed.Contains(part[..split]) || !Regex.IsMatch(part[(split+1)..],@"\A[-+A-Za-z0-9_.,/]+\z"))
                throw new Exception($"-{key}で指定できないキーまたは値です: {part}");
            result[part[..split]]=part[(split+1)..];
        }
        return result;
    }
    internal static List<(string Key,string Value)> Parse(EncoderSettings s)
    {
        var result=new List<(string,string)>();if(!Active(s))return result;
        var tokens=Tokenize(s.ExtraVideoArguments);
        if(tokens.Count%2!=0)throw new Exception("追加パラメータは「-項目 値」の組で指定してください。");
        for(int i=0;i<tokens.Count;i+=2)
        {
            string key=Key(tokens[i]),value=tokens[i+1];
            if(!Supports(s,key))throw new Exception($"追加パラメータ {tokens[i]} は {s.Codec} / {s.Backend} では指定できません。");
            if(key is "x264-params" or "x265-params")Compound(key,value);
            else if(!Regex.IsMatch(value,@"\A[-+A-Za-z0-9_.,/]+\z"))throw new Exception($"追加パラメータ {tokens[i]} の値が不正です。");
            if(s.Backend=="CPU" && s.Passes==2 && key=="crf")throw new Exception("CPUの2パスでは -crf を追加指定できません。品質指定の1パスに変更してください。");
            result.Add((key,value));
        }
        return result;
    }
    internal static void Apply(List<string> args,EncoderSettings settings)
    {
        foreach(var (key,value) in Parse(settings))
        {
            string effective=value;
            for(int i=0;i<args.Count-1;i++)
            {
                if(args[i]!="-"+key && args[i]!="-"+key+":v")continue;
                if(key is "x264-params" or "x265-params")
                {
                    var merged=Compound(key,args[i+1]);foreach(var entry in Compound(key,value))merged[entry.Key]=entry.Value;
                    effective=string.Join(":",merged.Select(p=>p.Key+"="+p.Value));
                }
                args.RemoveRange(i,2);i--;
            }
            args.Add("-"+key+":v");args.Add(effective);
        }
    }
    // Windows command-line quoting for display/copy. Execution uses ArgumentList,
    // never a shell, regardless of the text shown here.
    internal static string Command(string exe,IEnumerable<string> args)=>string.Join(" ",new[]{exe}.Concat(args).Select(Quote));
    static string Quote(string value)
    {
        if(value.Length>0 && !value.Any(c=>char.IsWhiteSpace(c)||c=='"'))return value;
        var result=new StringBuilder("\"");int slashes=0;
        foreach(char c in value)
        {
            if(c=='\\'){slashes++;continue;}
            result.Append('\\',c=='"'?slashes*2+1:slashes);result.Append(c);slashes=0;
        }
        return result.Append('\\',slashes*2).Append('"').ToString();
    }
}
