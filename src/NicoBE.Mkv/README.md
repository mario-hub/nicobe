# NicoBEのMKV読み取りDLL

Windows x64向け。 reader.c と reader.h はNicoBEのMITライセンスです。FFmpeg 9.0.2の無改変ソースは ffmpeg-9.0.2.tar.xz に収録しています。FFmpegのDLLはLGPL 2.1以降で配布し、NicoBEのラッパーはこれらのDLLに動的リンクします。zlib フォルダにはTVTestソースから取得したzlib 1.3.2の展開に必要な無改変ソースとライセンスを収録しています。

build.ps1 に構成オプションを記載しています。Matroska demuxer、ローカルファイルプロトコル、zlibを有効にし、GPLおよびnonfreeの構成は無効です。zlibの展開処理はavformatに静的リンクします。bzip2圧縮はこの構成の対象外です。

展開したソースツリー内で相対パスを使ってコンパイルし、MSVCの `/experimental:deterministic` と `/pathmap` でヘッダー由来の診断用パスも相対化します。パス置換は環境変数 `CL` に渡し、元の絶対パスがFFmpegの構成文字列へ格納されるのを避けています。FFmpegのソース自体は変更しません。既存オブジェクトは `make clean` で除去してからビルドします。

生成したDLLは `.build/runtime` で検査し、絶対パスが残っていない場合だけ `runtime` を更新します。診断に使われる相対ファイル名は保持します。ビルド失敗時やパス検査で不合格になった場合、既存の `runtime` は更新しません。

再ビルドにはWindows SDK、MSVC x64、Git for Windowsと、同じMSYSランタイムで動くGNU Makeが必要です。配布DLLはMSYS2のmake 4.4.1-3を使用してビルドしました。

1. [MSYS2のGNU Makeパッケージ](https://packages.msys2.org/packages/make) を用意し、含まれる usr/bin/make.exe を展開します。
2. PowerShellでこのフォルダに移動し、`.\build.ps1 -MakeExe 'C:\path\to\make.exe'` を実行します。作業ファイルは .build、DLLとライセンス文書は runtime に出力します。
3. 再ビルドしたDLLをNicoBEで使用する場合は、実行ファイルの隣の mkv フォルダに同じABIのDLL一式を配置します。

公式ソースは [FFmpeg 9.0.2](https://ffmpeg.org/releases/ffmpeg-9.0.2.tar.xz)、配布に関する案内は [FFmpeg License and Legal Considerations](https://ffmpeg.org/legal.html) を参照してください。
