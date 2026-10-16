# Dynamic Campaign Japanese

Sea Powerの「Bungalow's Dynamic Campaign」を日本語化するMODのソースコードです。MODはSteam Workshopで配布します。利用者向けの説明文は [docs/workshop-description.txt](docs/workshop-description.txt) にあります。

表示用の文字列や艦名を日本語へ変換します。元MODのファイル、内部の艦名・識別子、セーブ形式は変更しません。

## 開発環境

- Windows版.NET SDK 10.0.401（`global.json` で指定）
- Python 3
- Sea PowerとAnchor Chainのインストール済みDLL

DLLの対象は.NET Standard 2.1です。ゲームとフレームワークのDLLはインストール先から参照し、配布物には含めません。

## 参照先の設定

プロジェクトルートで、設定の見本をコピーします。

```powershell
Copy-Item Directory.Build.local.props.example Directory.Build.local.props
```

`Directory.Build.local.props` のパスを自分のインストール先に変更します。このファイルはGit管理対象外です。

| 設定 | 指定するフォルダー |
| --- | --- |
| `SeaPowerDir` | Sea Powerのゲームフォルダー |
| `AnchorChainDir` | `AnchorChain.dll` があるフォルダー |
| `OriginalModDir` | `DynamicCampaignMod.dll` があるフォルダー（比較検証用） |

Visual Studioで編集する場合は `src/DynamicCampaignJapanese.csproj` を開きます。

## ビルドと梱包

プロジェクトルートで実行します。

```text
python tools/build.py
```

WSLではプロジェクトをWindowsドライブ上に置き、`python3 tools/build.py` を使用します。ローカル設定の参照先にはWindows形式のパスを指定します。

Windows版の `dotnet.exe` をPATHから探します。見つからない場合は `DOTNET_EXE` に実行ファイルのパスを指定してください。NuGetの外部パッケージは取得しません。

配布フォルダーは `artifacts/package/DynamicCampaignJapanese`、ZIPは `artifacts/DynamicCampaignJapanese.zip` に生成します。内容は `DynamicCampaignJapanese.dll`、`translation-data.json`、`campaign-ship-names.json`、`_info.ini`、`CREDITS.txt` の5ファイルです。生成物はGit管理対象外です。

キャンペーン独自の個艦名は `localization/campaign-ship-names.json` で管理します。`ships` 配下にユニットID、その下に元の艦名と日本語名の対訳を登録します。辞書は起動時に読み込み、表示時にゲーム本体の艦名対応表より優先して参照します。

## ソースの構成

- `src`：翻訳DLLのソースコード
- `localization`：翻訳データ
- `package`：配布用のMOD設定とクレジット
- `assets/workshop`：Workshopアイコンと編集用PSD
- `tools`：ビルド・比較検証用ツール
- `docs`：Workshop説明文と開発文書

検証の実施条件、検証内容、ゲーム上での確認手順は [docs/verification.md](docs/verification.md) を参照してください。
