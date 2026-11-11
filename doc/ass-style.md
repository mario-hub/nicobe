# EncoderのASS書式設定

GUIの「ASS書式設定」とCLIは、`encoder-settings.json` の `AssStyle` を共通で使用します。MKVの埋め込みASSと外部ASSに適用し、MP4内のオリジナルコメントは変更しません。フォントは同梱せず名前を保存します。

| 項目名 | 内容・範囲 | 初期値 |
| --- | --- | --- |
| `FontFamily` | フォント名。再生側にも必要 | `Yu Gothic UI` |
| `FontWeight` | 400／700／900。900は対応するフォント・字幕レンダラーで表示 | 700 |
| `OutlineLevel` | 0～5 → 幅0／1.5／2.5／4／6／8 | 1 |
| `ShadowLevel` | 0～5 → 右下へのずれ0／1／2／3／4／6 | 2 |
| `FontSizePercent` | 50～300％。100％は1080p基準で48 | 100 |
| `ScrollSpeedPercent` | 50～300％。100％は360 px/秒 | 100 |
| `LineSpacingPercent` | 0～100％。基準文字サイズに対する行間の余白 | 30 |
| `AccelerateLongComments` | 長文を1文字につき2％加速、最大2倍。追いつきを避けて配置 | true |
| `AdvanceSeconds` | −10～10秒。正の値は早く表示。冒頭より前に出る分はクリップ | 0 |
| `OpacityPercent` | 10～100％。影はさらに0.65倍の不透明度 | 90 |
| `FixedCommentSeconds` | 上・下固定コメントの表示時間、1～30秒 | 5 |
| `MaxVisibleComments` | 同時表示数、1～300。空き行がない場合も省略 | 100 |

たとえば既存の設定JSONへ、次の項目を追加できます。省略した書式項目には共通初期値が入ります。

```json
"AssStyle": {
  "FontFamily": "Yu Gothic UI",
  "FontWeight": 700,
  "FontSizePercent": 125,
  "ScrollSpeedPercent": 100,
  "LineSpacingPercent": 30,
  "OutlineLevel": 1,
  "ShadowLevel": 2,
  "AccelerateLongComments": true,
  "AdvanceSeconds": 0,
  "OpacityPercent": 90,
  "FixedCommentSeconds": 5,
  "MaxVisibleComments": 100
}
```

GUIの「保存」はエンコード設定・ASS書式設定・基本設定をまとめて保存します。CLIは `--settings` の指定先、または実行ファイル側の設定ファイルを読み、書き換えません。旧 `CommentSizePercent` は `AssStyle` がない場合だけ読み込み、新たな保存は `AssStyle.FontSizePercent` に統一します。

## 表示と再生

フォント・縁取り・影・不透明度はASSスタイルへ、400／700／900の太さは `\b`、速度は `\move`、時間調整・固定表示時間はDialogueの開始・終了時刻へ反映します。[ASSの太さ指定](https://aegisub.org/docs/latest/ass_tags/)も参照してください。フォントのセル高さは [GlyphTypeface.Height](https://learn.microsoft.com/en-us/dotnet/api/system.windows.media.glyphtypeface.height) から算出し、NicoBE読み戻し用の倍率をスタイル名に記録します。

通常のASS字幕として再生する場合、速度は動画の再生倍率に従います。NicoBEでコメントとして読み込む場合は、保存した相対サイズ・時刻・本文・色・表示位置の種類を読み取り、フォント・太さ・縁取り・影・速度・配置などはNicoBEの再生設定で描画します。カラー絵文字、NG設定、操作パネルなどはASS書式設定に含めません。

## 開発

- 製品初期値と縁取り・影の段階値は `src/Shared/CommentDisplayDefaults.cs` が定義元です。
- NicoBE／Encoderのcsprojは `src/Shared/*.cs` を直接コンパイルします。CLIはEncoderプロジェクトを参照します。
- `Config` と `AssStyleSettings` は共通初期値から初期化します。利用者の設定ファイル間で値を同期する仕組みではありません。
- ASS生成の入口は `AssComments.Write(..., AssStyleSettings)`。MKV格納・外部ASS保存の両方で同じ設定を渡します。
- `--ass-style-test` は書式・保存復元・初期値の一致を、CLIの `--self-test` は設定読み込みを確認します。テスト出力は隔離ビルド先内へ保存してください。
