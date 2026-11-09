using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace NicoBE;

public sealed record InstallProgress(string Message,double? Fraction);
public static class FfmpegInstaller
{
    public const string DownloadUrl="https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";
    public static string InstallRoot=>StorageLayout.Current.FfmpegDirectory;
    public static async Task<string> Install(HttpClient client,string root,IProgress<InstallProgress> progress,CancellationToken token)
    {
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(TimeSpan.FromMinutes(20));token=deadline.Token;
        root=Path.GetFullPath(root);Directory.CreateDirectory(root);
        if((File.GetAttributes(root)&FileAttributes.ReparsePoint)!=0)throw new IOException("FFmpegの保存先にリンクフォルダは使用できません。");
        string stage;
        do{stage=Path.Combine(root,".incoming-"+Guid.NewGuid().ToString("N")[..16]);}
        while(Directory.Exists(stage)||File.Exists(stage));
        Directory.CreateDirectory(stage);
        try
        {
            progress.Report(new("配布元のチェックサムを取得しています…",null));
            string checksum=await client.GetStringAsync(DownloadUrl+".sha256",token);
            var match=Regex.Match(checksum,@"\b[0-9a-fA-F]{64}\b");
            if(!match.Success)throw new IOException("配布元のチェックサムを取得できませんでした。");
            string archive=Path.Combine(stage,"download.zip");
            using(var response=await client.GetAsync(DownloadUrl,HttpCompletionOption.ResponseHeadersRead,token))
            {
                response.EnsureSuccessStatusCode();long? total=response.Content.Headers.ContentLength;
                await using var source=await response.Content.ReadAsStreamAsync(token);
                await using var output=new FileStream(archive,FileMode.CreateNew,FileAccess.Write,FileShare.None,131072,true);
                byte[] buffer=new byte[131072];long received=0;var update=System.Diagnostics.Stopwatch.StartNew();int count;
                while((count=await source.ReadAsync(buffer,token))>0)
                {
                    await output.WriteAsync(buffer.AsMemory(0,count),token);received+=count;
                    if(received>1024L*1024*1024)throw new IOException("ダウンロードサイズが上限を超えました。");
                    if(update.ElapsedMilliseconds>=100)
                    {
                        progress.Report(new($"ダウンロード中  {received/1048576.0:0.0} MB"+(total>0?$" / {total/1048576.0:0.0} MB":""),total>0?(double)received/total.Value:null));update.Restart();
                    }
                }
                if(total.HasValue && received!=total.Value)throw new IOException("ダウンロードが途中で終了しました。");
            }
            progress.Report(new("ファイルを検証しています…",null));
            await using(var input=File.OpenRead(archive))
            {
                string actual=Convert.ToHexString(await SHA256.HashDataAsync(input,token));
                if(!actual.Equals(match.Value,StringComparison.OrdinalIgnoreCase))throw new IOException("チェックサムが一致しません。配布元が更新された可能性があります。もう一度ダウンロードしてください。");
            }
            string unpacked=Path.Combine(stage,"content");Directory.CreateDirectory(unpacked);
            progress.Report(new("FFmpegを展開しています…",null));
            await Task.Run(()=>Extract(archive,unpacked,token),token);
            var executables=Directory.GetFiles(unpacked,"ffmpeg.exe",SearchOption.AllDirectories);
            if(executables.Length!=1)throw new IOException("ZIP内のffmpeg.exeを特定できません。");
            string executable=executables[0];
            if(!File.Exists(Path.Combine(Path.GetDirectoryName(executable)!,"ffprobe.exe")))throw new IOException("ZIP内にffprobe.exeがありません。");
            // Retain the distribution's versioned directory, without an extra installation ID layer.
            var entries=Directory.GetFileSystemEntries(unpacked);
            string installSource=entries.Length==1 && Directory.Exists(entries[0])?entries[0]:unpacked;
            string name=installSource==unpacked?"build":Path.GetFileName(installSource);
            string relative=Path.GetRelativePath(installSource,executable);
            string destination=Publish(installSource,root,name,token);
            return Path.Combine(destination,relative);
        }
        finally
        {
            // Only remove this install's private staging directory, never an existing installation.
            string safeStage=Path.GetFullPath(stage);
            if(safeStage.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase) && Directory.Exists(safeStage))
            {
                try{Directory.Delete(safeStage,true);}catch(IOException){}catch(UnauthorizedAccessException){}
            }
        }
    }
    static string Publish(string source,string root,string name,CancellationToken token)
    {
        for(int number=1;number<=9999;number++)
        {
            token.ThrowIfCancellationRequested();
            string destination=Path.Combine(root,name+(number==1?"":"-"+number.ToString("D4")));
            if(Directory.Exists(destination)||File.Exists(destination))continue;
            try{Directory.Move(source,destination);return destination;}
            catch(IOException) when(Directory.Exists(destination)||File.Exists(destination)){}
        }
        throw new IOException("FFmpegの保存先に空いているフォルダ名がありません。");
    }
    static void Extract(string archive,string destination,CancellationToken token)
    {
        string prefix=Path.GetFullPath(destination)+Path.DirectorySeparatorChar;
        using var zip=ZipFile.OpenRead(archive);long extracted=0;
        foreach(var entry in zip.Entries)
        {
            token.ThrowIfCancellationRequested();
            string target=Path.GetFullPath(Path.Combine(destination,entry.FullName));
            if(!target.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)||entry.FullName.Contains(':'))throw new IOException("ZIP内に不正なパスがあります。");
            extracted+=entry.Length;if(extracted>2L*1024*1024*1024)throw new IOException("展開サイズが上限を超えました。");
            if(entry.Name.Length==0){Directory.CreateDirectory(target);continue;}
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var input=entry.Open();using var output=new FileStream(target,FileMode.CreateNew);
            byte[] buffer=new byte[131072];int count;
            while((count=input.Read(buffer))>0){token.ThrowIfCancellationRequested();output.Write(buffer,0,count);}
        }
    }
}
