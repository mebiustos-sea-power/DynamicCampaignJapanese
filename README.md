# Dynamic Campaign Japanese

Sea Powerの「Bungalow's Dynamic Campaign 0.24.3」を日本語化するMODのソースコードです。MODの配布はSteam Workshopを前提とし、このREADMEでは開発環境、ビルド、検証について説明します。Workshopに掲載する利用者向けの説明文は `docs/workshop-description.txt` に保存します。

## 開発環境

- Windows版.NET SDK 10.0.401（`global.json` で指定）
- Python 3
- Sea PowerとAnchor Chain 1.1.0のインストール済みDLL
- 実行・比較検証用のBungalow's Dynamic Campaign 0.24.3

配布DLLの対象は.NET Standard 2.1です。ゲームとフレームワークのDLLは開発環境から参照し、配布物には含めません。

Visual Studioで編集する場合は `src/DynamicCampaignJapanese.csproj` を開きます。ビルドは.NET CLIを使用します。参照先は `Directory.Build.props` の `SeaPowerDir`、`AnchorChainDir`、`OriginalModDir` で設定します。検証プログラムの参照先は `tools/Verifier/Program.cs` にあります。

## フォルダー構成

- `src`：翻訳DLLのソースコード
- `localization/translation-data.json`：英語と日本語の翻訳データ
- `package`：配布用のMOD設定とクレジット
- `docs/workshop-description.txt`：Workshopの説明文
- `tools`：ビルド・比較検証用スクリプト
- `artifacts`：生成物、ログ、比較用データ（Git管理対象外）

## ビルドと梱包

プロジェクトルートで実行します。

```text
python tools/build.py
```

WSLでは `python3 tools/build.py` を使用します。NuGetの外部パッケージは取得しません。

配布物は `artifacts/package/DynamicCampaignJapanese`、確認・受け渡し用のZIPは `artifacts/DynamicCampaignJapanese.zip` に生成します。ビルドのたびに配布フォルダーを作り直し、古いファイルが残らないようにします。

配布物には `DynamicCampaignJapanese.dll`、`translation-data.json`、`_info.ini`、`CREDITS.txt` の4ファイルを含めます。利用者向けの案内はWorkshopの説明欄に掲載します。

## 比較検証

```text
python tools/verify.py
```

WSLでは `python3 tools/verify.py` を使用します。検証には移行時に取得した `artifacts/baseline` が必要です。従来版のバックアップ、表示データ、既存セーブのコピーはGit管理・配布の対象外です。

検証では21画面のXAML、翻訳処理の命令列、キャンペーン設定、キャッシュ済みの表示名、バインディング用プロパティ名、既存セーブの読み込みを比較します。結果は `artifacts/verification-report.json` に出力します。ゲーム画面の描画とセーブからの再開は、ゲーム上で別途確認します。

ローカルで動作確認する際は、ゲームを完全に終了してから生成した配布物を `Sea Power_Data/StreamingAssets/DynamicCampaignJapanese` に配置し、再起動します。旧DLLのバックアップはゲームのMOD読み込み対象となるフォルダーの外に保管します。

## 実装上の前提

動作確認の基準は元MOD 0.24.3です。プログラム内では元MODのバージョンによる制限を設けず、見つからない翻訳対象はログに記録してスキップします。

翻訳パッチは、元MODがメインメニューで行う初期化の完了後に適用します。画面ライブラリの準備前にUI型をパッチすると型の初期化に失敗するため、この順序を守ります。適用成功時は起動ログに `Independent Japanese localization 2.0.0 active` が出力されます。

キャンペーンIDとユニット識別子は保持し、セーブ形式は変更しません。個艦名・艦級名の追加翻訳は現在の実装範囲に含めません。
