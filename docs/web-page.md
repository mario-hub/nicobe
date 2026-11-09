# 説明ページの公開・検証

## 公開URLと構成

正規URLは `https://www.moreread.net/apps/nicobe/index.html`。canonicalとog:urlを一致させている。アプリ2種類のSoftwareApplication JSON-LDにもURLとダウンロード先を記載した。架空の評価・レビューは追加していない。

原本は `src/Website/index.html` に置く。CSS・アイコンはHTMLに内包。GA4のみ公開時に外部スクリプトを読み込む。配布ZIPはHTMLと同じ階層へ置く。HTMLの検証ではZIPの存在・内容を確認せず、リンク先の名前と形式だけを検査する。

## リリース時のHTML生成

`release/create-release.bat` は `tools/create-release-page.ps1` を呼び、バージョン・公開日・GA4設定を組み込んだ `index.html` をZIPと同じ出力先へ保存する。バージョンと日付は `release/version.txt` の `NicoBE`／`NicoBE Encoder` と、それぞれの `Date` 行で管理する。不正・欠落・重複した定義はエラーにする。

HTMLのみ生成する場合は `tools/create-release-page.ps1 -OutputPath <既存の出力フォルダ内のHTMLパス>` を使用する。テンプレートは上書きしない。`NICOBE_PAGE_PATH` に生成したHTMLの絶対パスを指定すると、表示検証の対象を変更できる。

## 関連ツールと説明の範囲

関連ツールの説明は以下の一次資料を参照する。機能の関連性を示すものであり、検索順位や検索ボリュームを保証するものではない。

| 関連語 | ページでの扱い・確認資料 |
| --- | --- |
| TVTest、NicoJK、ニコニコ実況 | 録画と保存済み実況ログの利用例。[TVTest](https://github.com/DBCTRADO/TVTest)、[NicoJK](https://github.com/xtne6f/NicoJK) |
| EDCB、EpgDataCap_Bon、NicojCatch | TS録画環境とNicoJK形式のログ。録画アプリとの直接連携機能があるとは書かない。[EDCB](https://github.com/xtne6f/EDCB)、[NicoJKの説明](https://github.com/xtne6f/NicoJK) |
| Amatsukaze、NicoConvAss、ASS字幕 | 外部コメントASSの利用説明。[Amatsukaze](https://github.com/rigaya/Amatsukaze) |
| TvRock、DTV、TS抜き、MPEG-TS | 既存の録画ファイルを利用するFAQで説明。受信・録画・復号・コメント取得機能を提供するという説明はしない |
| MP4、MKV、H.264、H.265／HEVC、AV1、NVENC、QSV、AMF | 実装済みの再生形式とエンコード機能の説明で使用 |

title・description・見出し・本文を整えた。Googleが検索ランキングに使用しないmeta keywordsは追加せず、読み手に役立つ本文へ自然に反映している。[GoogleのSEOガイド](https://developers.google.com/search/docs/fundamentals/seo-starter-guide)、[Googleのメタタグ仕様](https://developers.google.com/search/docs/crawling-indexing/special-tags)

## GA4

- 測定IDはGit対象外の `.local/ga4-id.txt` に1行で保存する。リリース時にHTMLへ組み込み、未設定・不正な場合は警告を出して計測なしで生成する。リポジトリのHTMLには測定IDを含めない。
- HTTP/HTTPSの `www.moreread.net`／`moreread.net` でのみ読み込む。標準以外のポート、file URL、localhost、別ドメイン、preview等のパス、noindexページは除外。
- 同じ測定IDの設定済みキュー、Googleタグの存在、初期化済みフラグを確認し、重複初期化を避ける。
- configが送る自動page_viewを使い、手動page_viewは重ねて送らない。[Googleタグの設定](https://developers.google.com/tag-platform/gtagjs/configure)
- FAQにアクセス解析の説明とGoogleのデータ利用説明へのリンクを置いた。
- ローカルの検証では、模擬ブラウザー環境で計測条件と二重初期化防止を確認し、Googleへイベントを送信しない。公開後の実測データ到着はGA4側で確認する。

## 表示・静的検証

`node tools/check-page.cjs` は画面を開かないヘッドレス検証。Chrome、なければEdgeを利用する。制御にはローカルパイプを使用し、デバッグ用TCPポートは開かない。

PC幅1440px、スマホ幅390px・320pxの横あふれ、画像、ページ内リンク、詳細の開閉、SEOメタデータ、GA4ガード、ダウンロードリンクの形式を検査する。アイコン内BE文字の下端（256px画像のy=205）と見出しのベースラインを測り、1px未満の差であることも確認する。

検証結果と画像は `temp/test-results/web-page`、ブラウザーの一時プロファイルは `temp/web-check-*` に作る。確認後は、この実行で作った分を削除する。制約のある開発環境でChromeのサンドボックス初期化が失敗する場合のみ、ローカルの既知のHTML検証に限って `NICOBE_HEADLESS_NO_SANDBOX=1` を指定できる。通常は指定しない。
