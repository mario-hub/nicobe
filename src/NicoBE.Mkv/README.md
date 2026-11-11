# NicoBEのMKV読み取りDLL

Windows x64向け。 reader.c と reader.h はNicoBEのMITライセンスです。FFmpeg 9.0.2の無改変ソースは ffmpeg-9.0.2.tar.xz に収録しています。FFmpegのDLLはLGPL 2.1以降で配布し、NicoBEのラッパーはこれらのDLLに動的リンクします。zlib フォルダにはTVTestソースから取得したzlib 1.3.2の展開に必要な無改変ソースとライセンスを収録しています。

build.ps1 に構成オプションを記載しています。Matroska demuxer、ローカルファイルプロトコル、zlibを有効にし、GPLおよびnonfreeの構成は無効です。zlibの展開処理はavformatに静的リンクします。bzip2圧縮はこの構成の対象外です。

## 配布用の対応ソースZIP

リポジトリには元のFFmpegアーカイブを保持します。`tools/prepare-mkv-source.ps1` は、配布ZIP用に未使用のコーデック・フィルター・形式・デバイス・変換処理の実装ファイル、文書用ファイル、FATEの入力・期待値データを省きます。使用する実装、全ヘッダー、構成・ビルド定義、ライセンスは保持し、残すファイルの内容は変更しません。

配布ZIPでは `ffmpeg-9.0.2-nicobe.tar.xz` と `ffmpeg-source.json` を使用します。JSONには元アーカイブと縮小版のSHA-256、除外した全パスを記載します。`build.ps1` はJSONを検証して縮小版を展開します。JSONがないリポジトリ内では従来どおり元アーカイブを使用します。構成を変更して別のコーデック等を有効にする場合は、元アーカイブが必要です。

`ffmpeg-required-sources.txt` は、同じMSVC構成で実際にコンパイルされた翻訳単位と `.d` の依存ファイル、および `configure` が読む機能一覧から作成した保持リストです。FFmpegのバージョンや構成を変える場合は、元アーカイブからのビルドでリストを更新し、縮小版からのクリーンビルドも確認してください。現構成で使わないヘッダーも、構成検査と公開インターフェースのため保持します。

## 再ビルド

展開したソースツリー内で相対パスを使ってコンパイルし、MSVCの `/experimental:deterministic` と `/pathmap` でヘッダー由来の診断用パスも相対化します。パス置換は環境変数 `CL` に渡し、元の絶対パスがFFmpegの構成文字列へ格納されるのを避けています。FFmpegのソース自体は変更しません。既存オブジェクトは `make clean` で除去してからビルドします。

生成したDLLは `.build/runtime` で検査し、絶対パスが残っていない場合だけ `runtime` を更新します。診断に使われる相対ファイル名は保持します。ビルド失敗時やパス検査で不合格になった場合、既存の `runtime` は更新しません。

再ビルドにはWindows SDK、MSVC x64、Git for Windowsと、同じMSYSランタイムで動くGNU Makeが必要です。配布DLLはMSYS2のmake 4.4.1-3を使用してビルドしました。

1. [MSYS2のGNU Makeパッケージ](https://packages.msys2.org/packages/make) を用意し、含まれる usr/bin/make.exe を展開します。
2. PowerShellでこのフォルダに移動し、`.\build.ps1 -MakeExe 'C:\path\to\make.exe'` を実行します。作業ファイルは .build、DLLとライセンス文書は runtime に出力します。
3. 再ビルドしたDLLをNicoBEで使用する場合は、実行ファイルの隣の mkv フォルダに同じABIのDLL一式を配置します。

公式ソースは [FFmpeg 9.0.2](https://ffmpeg.org/releases/ffmpeg-9.0.2.tar.xz)、配布に関する案内は [FFmpeg License and Legal Considerations](https://ffmpeg.org/legal.html) を参照してください。
