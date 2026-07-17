# TapoCtrl

[English](README_en.md) | **日本語**

Windows から TP-Link Tapo デバイスを監視・操作するための WPF デスクトップアプリです。電力、温度、湿度、スイッチ状態をパネルやグラフで表示し、デスクトップ、タスクトレイ、ローカル Web 画面から操作できます。

現在のバージョン: **v0.0.82**

## 主な機能

- LAN 上の Tapo デバイスを探索し、状態を定期取得
- 電力、温度、湿度、ON/OFF 状態のパネル表示
- 直近データのグラフ表示と電力料金の概算
- タブ、パネル配置、サイズ、文字色などのカスタマイズ
- タスクトレイとミニパネルからの電源操作
- ローカル Web 画面と JSON／電源操作 API
- 接続失敗時も最後に取得できたスナップショットを保持

## 対応環境

- Windows 10/11 x64
- Python 3
- Python パッケージ: `python-kasa`、`tapo`
- ソースからビルドする場合: .NET 8 SDK 以降

## インストール

### Release を使う

GitHub の [Releases](../../releases) から最新版の ZIP を取得して展開し、`TapoCtrl.exe` を起動してください。アプリ本体には .NET ランタイムが含まれますが、Tapo 通信には Python 3 が必要です。

初回起動時に Python パッケージが不足している場合は、アプリ内の案内からインストールできます。手動で入れる場合は次を実行します。

```powershell
python -m pip install --user --upgrade python-kasa tapo
```

### ソースからビルドする

```powershell
git clone https://github.com/cyfomix-ui/TapoCtrl.git
cd TapoCtrl
.\Build.ps1
```

自己完結型の単一 EXE を生成する場合:

```powershell
.\Build.ps1 -Publish
```

生成先は `TapoCtrl\bin\Release\net8.0-windows\win-x64\publish` です。

## 初期設定

1. Python 3 と必要パッケージを用意します。
2. TapoCtrl の設定画面で TP-Link ID（メールアドレス）とパスワードを入力します。
3. 必要に応じて Hub の IP アドレスや Python 実行ファイルのパスを指定します。
4. デバイス探索後、パネル配置や取得間隔を調整します。

認証情報は `%LOCALAPPDATA%\TapoCtrl\credentials.bin` に Windows DPAPI（現在のユーザー単位）で暗号化して保存され、リポジトリや設定 JSON には保存されません。

## Web 画面と API

既定のポートは `8080` です。

- Web 画面: `http://127.0.0.1:8080/`
- デバイス JSON: `http://127.0.0.1:8080/api/devices`
- 電源 ON: `/api/power?id=デバイスIDまたは名前&state=on`
- 電源 OFF: `/api/power?id=デバイスIDまたは名前&state=off`
- IP 直接指定: `/api/power?ip=192.168.1.50&state=on`

### セキュリティ上の注意

この HTTP サービス自体には認証機能がありません。`0.0.0.0` へバインドすると同一 LAN の端末からアクセスでき、電源操作 API も利用可能になります。信頼できるネットワーク以外では公開せず、外部公開が必要な場合は認証付きリバースプロキシや認証付きトンネルを使用してください。

Windows Firewall の受信規則が必要な場合は、管理者 PowerShell で次を実行します。

```powershell
.\Allow_TapoCtrl_WebServer_Firewall.ps1
```

## データ保存先

設定、履歴、認証情報は `%LOCALAPPDATA%\TapoCtrl` に保存されます。これらのローカルデータは Git の公開対象に含まれません。

## 最新版 v0.0.82 の変更点

- デスクトップ版の24時間グラフを、Web版と同じ統計カード形式へ更新
- 温湿度グラフへ温度・湿度の二軸表示、凡例、6項目の統計カードを追加
- グラフ表示中の履歴を1分ごとに自動更新
- 履歴を測定項目別に保存し、旧形式の履歴も互換読込
- Webダッシュボードを1分ごとに自動更新
- PNG／ICOアイコンを明るい紫系のデザインへ更新

## 注意事項

- 対応状況は Tapo 機種、ファームウェア、`python-kasa`／`tapo` のバージョンにより異なる場合があります。
- 電源操作や LAN 公開は、対象ネットワークと機器を管理する権限がある環境でのみ使用してください。
- 本リポジトリには現時点で明示的なライセンスを設定していません。
