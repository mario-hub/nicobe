# FFmpeg追加パラメータ

GUIのエンコード設定とCLIのJSONで共通の検証・引数生成を使用します。`ExtraVideoArgumentsEnabled`（初期値false）と `ExtraVideoArguments`（初期値空文字）を保存します。有効かつ空白以外の入力がある場合だけ適用します。

## 入力と対応範囲

`-項目 値` の組を空白・改行で区切ります。単引用符・二重引用符で値を囲めます。シェルは起動せず `ProcessStartInfo.ArgumentList` で渡します。リダイレクトやパイプ、環境変数展開は使用できません。最大16,384文字です。

各オプションは末尾の `:v` を省略可能です。動画トラックは1本のため、`:v:0`などの別のストリーム指定は受け付けません。生成時は `:v` に統一します。

| 処理方式 | 対応項目（先頭の `-` を省略） |
| --- | --- |
| 共通 | b、maxrate、bufsize、g、bf、refs、qmin、qmax、threads |
| CPU H.264／H.265 | preset、tune、crf、x264-params／x265-params（選択した映像形式のみ） |
| CPU AV1 | cpu-used、row-mt、crf、lag-in-frames、aq-mode |
| NVENC | preset、tune、rc、cq、rc-lookahead、spatial-aq、temporal-aq、aq-strength、b_ref_mode |
| QSV | preset、global_quality、look_ahead、look_ahead_depth |
| AMF | quality、rc、qp_i、qp_p、qp_b |

表は入力を受け付ける範囲です。各値と機能が実際に使えるかは、FFmpegのビルド・コーデック・GPUに依存します。FFmpegの失敗や未使用オプションの通知はエラーとして扱い、冒頭と末尾の診断をGUI／CLIに、全体を処理ログに残します。

`x264-params` は aq-mode、aq-strength、psy-rd、rc-lookahead、bframes、b-adapt、b-pyramid、keyint、min-keyint、scenecut、ref、deblock、me、subme、trellis に対応します。`x265-params` はこれらに加えて psy-rdoq、rd、rdoq-level、sao、strong-intra-smoothing、pools、frame-threads を受け付けます。値は `キー=値:キー=値` で記載します。コーデックが実装していないキー・値は使用できません。

## 優先順位と工程

- 同じ項目についてアプリ生成分を取り除き、追加指定を使います。追加指定内の重複は最後の値を使用します。画質モードの異なる項目同士（例：crfとb）の関係はFFmpegの解釈に従うため、GUIの画質モードと整合する値を指定してください。
- `x264-params`／`x265-params` はキーごとに統合します。例えばx265の `aq-mode=2` だけを追加しても、アプリ標準の `pools=8:frame-threads=2` は保持します。
- CPUの2パスは両工程に適用します。CPU 2パスでの `crf` は拒否します。パス数・統計ファイルの管理はアプリが行います。
- 入出力、トラック選択、音声、コーデック選択、解像度、FPS、フィルター、カット、時刻、メタデータ、進捗出力などは指定できません。コメント格納用の再多重化やffprobeにも追加指定は渡しません。
- 追加指定が有効で空でない場合、GPU初期化失敗時にCPUへ自動変更しません。利用者が処理方式と追加指定を見直します。

「引数を表示」の設定プレビューは入出力名・トラック番号が例です。エンコード後は直近の実行引数も表示します。CPU 2パスの各工程の実際の引数は処理ログに保存します。
