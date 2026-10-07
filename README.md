# EncodeBridge v1.0.0

TvAIrで録画した番組を外部の変換アプリへ自動で引き渡すためのPluginです。

TvAIrで録画結果が確定したあとに設定した変換アプリを起動して動画変換を行います。

## 動作環境

- TvAIr 1.2.4 以上
- Plugin SDK / Host Contract 1.1.14 対応環境
- Windows
- .NET 8
- Visual Studio 2022（ソースからビルドする場合）

## インストール方法

1. 配布ZIPを展開します。
2. `EncodeBridge.sln` をVisual Studio 2022で開きます。
3. 使用しているTvAIrに合わせて `Release | x64` または `Release | x86` を選択してソリューションをビルドします。
4. TvAIrを終了します。
5. 生成された `EncodeBridge.dll` をTvAIrのPluginフォルダ直下へコピーします。
6. TvAIrを起動します。
7. Pluginメニューに「EncodeBridge」が表示されることを確認します。

### 生成されるDLL

Release x64の場合:

```text
EncodeBridge\bin\x64\Release\net8.0-windows\EncodeBridge.dll
```

Release x86の場合:

```text
EncodeBridge\bin\x86\Release\net8.0-windows\EncodeBridge.dll
```

ソリューションのビルドでは `TvAIrPlugin.dll` も生成されますが、これはSDK契約プロジェクトのビルド出力です。EncodeBridgeのインストール対象は `EncodeBridge.dll` だけです。

### TvAIrへの配置先

`TvAIr.exe` があるフォルダを基準に次の場所へ配置します。

```text
<TvAIr.exeのあるフォルダ>\Plugins\EncodeBridge.dll
```

例:

```text
TvAIr\Plugins\EncodeBridge.dll
```

更新時もTvAIrを終了してから既存の `EncodeBridge.dll` を新しいDLLへ置き換えてください。

設定や履歴などの保存データを削除する必要はありません。

## 対応する変換アプリ

以下の変換アプリに対応しています。

- NVEncC
- QSVEncC
- VCEEncC
- FFmpeg
- HandBrakeCLI
- Amatsukaze

AviUtl / AviUtl2のGUI自動操作には対応していません。

## 基本的な使い方

### 1. EncodeBridgeを開く

TvAIrのPluginメニューから「EncodeBridge」を開きます。

### 2. 自動変換を設定する

「自動変換」をONにすると録画結果が確定したあとに自動で変換処理へ送られます。

OFFの場合は自動変換しません。

### 3. 変換アプリを選ぶ

「変換アプリ」から使用するアプリを選択します。

### 4. 実行ファイルを指定する

「実行ファイル」の参照ボタンから使用する変換アプリの実行ファイルを指定します。

例:

- `NVEncC64.exe`
- `QSVEncC64.exe`
- `VCEEncC64.exe`
- `ffmpeg.exe`
- `HandBrakeCLI.exe`

変換アプリを変更した場合は種類の異なる実行ファイルをそのまま引き継ぎません。

### 5. 品質を選ぶ

「品質」から次の3段階を選択できます。

- 高画質設定
- 標準設定
- 少容量設定

各変換アプリに応じた品質設定を使用します。

- NVEncC: QVBR
- QSVEncC: ICQ
- VCEEncC: QVBR
- FFmpeg: CRF
- HandBrakeCLI: RF

### 6. 映像設定を選ぶ

使用する映像コーデックなどを設定します。

設定できる内容は選択した変換アプリによって異なります。

### 7. 出力形式を選ぶ

出力形式を選択します。

主に以下を利用できます。

- MP4
- MKV

入力映像の解像度は原則としてそのまま維持します。

1920×1080の映像は1920×1080のまま出力します。

1440×1080などの放送素材ではアスペクト比情報を考慮して処理します。

### 8. 保存先を指定する

変換後ファイルの保存先を指定します。

保存先を指定しない場合は元の録画ファイルを基準に出力先が決まります。

## 自動変換の流れ

自動変換がONの場合は次の順序で処理します。

1. TvAIrで録画
2. 録画結果確定
3. EncodeBridgeが録画結果を受信
4. 変換待ちへ登録
5. 外部変換アプリを起動
6. 変換処理
7. 出力ファイル確定
8. 完了

録画中のファイルを途中から変換することはありません。

TvAIr側で録画結果が確定したあとに処理を開始します。

## 変換中の動作

EncodeBridgeのウィンドウを閉じても実行中の変換処理は継続します。

ウィンドウ表示中のみ画面表示を定期更新します。

閉じている間は不要な画面更新を行いません。

## 元の録画ファイルについて

EncodeBridgeは元の録画ファイルを自動で削除・移動・名前変更しません。

変換後も元ファイルの管理はTvAIrおよびユーザー側の運用に従います。

## 注意事項

- 使用する変換アプリは別途用意してください。
- 対応コーデックや利用条件は各変換アプリの仕様に従います。
- GPUエンコードを利用する場合は対応GPUとドライバが必要です。
- 外部変換アプリでエラーが発生した場合は各アプリのログも確認してください。

## 更新履歴

### v1.0.0

- EncodeBridge 初回リリース

## ライセンス

EncodeBridge本体のライセンスはZIP直下の `LICENSE` を正本とします。

`TvAIrPlugin/` 以下にはTvAIr Plugin SDK由来の契約コードを同梱しています。これらSDK由来コードには `SDK_LICENSE.txt` が適用されます。

`SDK_LICENSE.txt` はEncodeBridge本体のライセンスを置き換えるものではありません。EncodeBridge本体には `LICENSE` が適用され、SDK由来コードには `SDK_LICENSE.txt` が適用されます。
