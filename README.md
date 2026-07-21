# TapoCtrl

[English](README_en.md) | **日本語**

Windows から TP-Link Tapo デバイスを監視・操作するための WPF デスクトップアプリです。電力、温度、湿度、スイッチ状態をパネルやグラフで表示し、デスクトップ、タスクトレイ、ローカル Web 画面から操作できます。

現在のバージョン: **v0.1.01**

## メインパネル

![TapoCtrlのメインパネル](docs/images/MainPanel.png)

## 主な機能

- LAN 上の Tapo デバイスを探索し、状態を定期取得
- 電力、温度、湿度、ON/OFF 状態のパネル表示
- 日付を選択できる個別・系列グラフと電力量・電力料金の集計
- タブ、パネル配置、サイズ、文字色などのカスタマイズ
- タスクトレイとミニパネルからの電源操作
- 閲覧用と操作用に分かれたローカル Web ダッシュボード、JSON／電源操作 API
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

- 操作用 Web 画面: `http://127.0.0.1:8080/Ctrl/`
- 閲覧用 Web 画面: `http://127.0.0.1:8080/View/`
- デバイス JSON: `http://127.0.0.1:8080/api/devices`
- ローカル連携用電源操作: `POST /api/power?id=デバイスIDまたは完全な名前&state=on|off`
- IP 直接指定: `POST /api/power?ip=192.168.1.50&state=on|off`

### セキュリティ上の注意

この HTTP サービス自体にはユーザー認証機能がありません。`/api/power` はループバック接続専用で、LAN、Tailscale、スマートフォンなど別端末からの電源操作は `/Ctrl/` を使用します。`/Ctrl/` に接続できる端末は Tapo スイッチを操作できるため、`0.0.0.0` へバインドする場合は信頼できる LAN または Tailscale 内に限定してください。ルーターのポート開放によるインターネット直接公開は行わないでください。

Windows Firewall の受信規則が必要な場合は、管理者 PowerShell で次を実行します。

```powershell
.\Allow_TapoCtrl_WebServer_Firewall.ps1
```

## データ保存先

設定、履歴、認証情報、動作ログは `%LOCALAPPDATA%\TapoCtrl` に保存されます。これらのローカルデータは Git の公開対象に含まれません。

- 日次ログ: `%LOCALAPPDATA%\TapoCtrl\logs\TapoCtrl_YYYYMMDD.log`
- 週次アーカイブ: `%LOCALAPPDATA%\TapoCtrl\logs\archive\`

ログの有効／無効、レベル、関数入口の詳細ログは設定画面で変更できます。`Trace`は大量に出力されるため、問題調査時だけ使用してください。

## 最新版 v0.1.01 の変更点

- 個別・系列グラフに履歴日付の選択を追加し、電力系に合計系列と本日／月間の電力量・概算料金を追加
- デバイスID基準の固定色をローカル・Webの名前とグラフで共通化
- Webダッシュボードを `/Ctrl/` と `/View/` に分離し、選択した最大4台の小型グラフ、現在値、取得時刻、offline／stale状態を表示
- グラフ画面の例外表示、デバイスグループの折りたたみ、電力サマリーと取得遅延表示を改善
- ローカル電源操作APIの対象照合とループバック制限を強化

## 注意事項

- 対応状況は Tapo 機種、ファームウェア、`python-kasa`／`tapo` のバージョンにより異なる場合があります。
- 電源操作や LAN 公開は、対象ネットワークと機器を管理する権限がある環境でのみ使用してください。
- 本リポジトリには現時点で明示的なライセンスを設定していません。
