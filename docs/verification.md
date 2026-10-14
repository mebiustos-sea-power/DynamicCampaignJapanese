# 開発時の検証

## 比較検証の実施条件

`tools/verify.py` は、独立DLLへの移行時に取得した基準データと現在の実装を比較する開発用ツールです。通常のビルドには不要です。

基準データには従来版のDLL、翻訳データ、実行結果、既存セーブのコピーが必要です。これらはリポジトリや配布物に含めていません。基準データを保管していない環境では、この比較検証を実行できません。

実行時には、READMEの手順で `Directory.Build.local.props` を用意し、`SeaPowerDir`、`AnchorChainDir`、`OriginalModDir` を設定します。ビルドと検証プログラムは同じ設定を使います。

基準データの構成は次のとおりです。

```text
baseline/
  runtime-output.json
  installed-package/
    DynamicCampaignJapanese.dll
    rules.json
    texts.json
    campaign-texts.json
  saves/
    （検証するキャンペーンのセーブJSON。サブフォルダーも対象）
```

`runtime-output.json` には、移行時に取得したXAMLの原文・翻訳結果と、キャンペーン設定の原文・翻訳結果を使用します。`installed-package` には、その結果を取得した従来版DLLと旧形式の翻訳データを置きます。現在のDLLや `translation-data.json` を置いて基準データの代わりにすることはできません。

## 実行

プロジェクトルートで実行します。

```text
python tools/verify.py --baseline artifacts/baseline
```

WSLでは `python3` を使用し、基準データをWindowsドライブ上に置きます。`--baseline` で基準データの保管先を変更できます。省略時はプロジェクト内の `artifacts/baseline` を使います。基準データが不足している場合は、エラーとして不足するファイルを表示します。

検証対象は次のとおりです。

- XAML、翻訳処理の命令列、キャンペーン設定、キャッシュ済み表示名の従来版との一致
- バインディング用プロパティ名の維持
- 初期化失敗時のログと、翻訳対象が見つからない場合の動作
- 個艦名・艦級名の表示変換、独自名称の維持、NATO名称設定と翻訳データの上書きへの対応
- 既存セーブの読み込みと、表示変換前後の保存内容の一致

結果は `artifacts/verification-report.json` に出力します。ログと検証用に生成したデータも `artifacts` に保存します。

## ゲーム上での確認

ゲームを完全に終了してから、生成した配布フォルダーを `Sea Power_Data/StreamingAssets/DynamicCampaignJapanese` に配置して再起動します。旧DLLのバックアップは、ゲームがMODを読み込むフォルダーの外に保管します。

日本語表示、新規キャンペーンの開始、既存セーブからの再開を確認します。個艦名と艦級名は、基地・艦隊・詳細・編成・出撃などの画面で確認します。比較検証だけでは、実ゲームでの描画と操作は確認できません。

## 実装上の前提

動作確認の基準は元MOD 0.24.3です。プログラム内では元MODのバージョンによる制限を設けず、見つからない翻訳対象はログに記録してスキップします。

翻訳パッチは、元MODのメインメニュー初期化が完了してから適用します。画面ライブラリの準備前にUI型をパッチすると型の初期化に失敗するため、この順序を守ります。

適用時は、起動ログに `Independent Japanese localization` と `Japanese ship-name display translation active` を含むメッセージが出力されます。初期化や個別パッチが失敗した場合もログに記録します。

個艦名・艦級名は、ゲームが読み込む英語・日本語の艦名データを対応付け、画面表示時に変換します。対応が確認できない独自名称を維持し、内部の艦名、キャンペーンID、ユニット識別子、セーブ形式を変更しない構成です。
