# TapoCtrl

**English** | [日本語](README.md)

TapoCtrl is a Windows WPF desktop application for monitoring and controlling TP-Link Tapo devices. It presents power, temperature, humidity, and switch states in customizable panels and graphs, with controls available from the desktop, system tray, and a local web interface.

Current version: **v0.0.82**

## Main panel

![TapoCtrl main panel](docs/images/MainPanel.png)

## Features

- Discovers Tapo devices on the LAN and polls their state
- Displays power, temperature, humidity, and on/off status
- Shows recent data in graphs and estimates electricity cost
- Customizable tabs, panel positions, sizes, and colors
- Power control from the system tray and mini panel
- Local web dashboard plus JSON and power-control APIs
- Retains the last successful snapshot during temporary connection failures

## Requirements

- Windows 10/11 x64
- Python 3
- Python packages: `python-kasa` and `tapo`
- To build from source: .NET 8 SDK or later

## Installation

### Use a release build

Download the latest ZIP from [Releases](../../releases), extract it, and run `TapoCtrl.exe`. The application binary includes the .NET runtime, but Python 3 is still required for Tapo communication.

If the Python packages are missing, the application can guide you through their installation. To install them manually:

```powershell
python -m pip install --user --upgrade python-kasa tapo
```

### Build from source

```powershell
git clone https://github.com/cyfomix-ui/TapoCtrl.git
cd TapoCtrl
.\Build.ps1
```

To produce a self-contained single-file executable:

```powershell
.\Build.ps1 -Publish
```

The output is written to `TapoCtrl\bin\Release\net8.0-windows\win-x64\publish`.

## Initial setup

1. Install Python 3 and the required packages.
2. Enter your TP-Link ID (email address) and password in TapoCtrl settings.
3. If necessary, specify hub IP addresses and the Python executable path.
4. After device discovery, adjust the panel layout and polling intervals.

Credentials are encrypted for the current Windows user with DPAPI and stored in `%LOCALAPPDATA%\TapoCtrl\credentials.bin`. They are not written to the repository or the settings JSON file.

## Web interface and API

The default port is `8080`.

- Web interface: `http://127.0.0.1:8080/`
- Device JSON: `http://127.0.0.1:8080/api/devices`
- Power on: `/api/power?id=device-id-or-name&state=on`
- Power off: `/api/power?id=device-id-or-name&state=off`
- Direct IP: `/api/power?ip=192.168.1.50&state=on`

### Security notice

The HTTP service does not provide authentication. Binding it to `0.0.0.0` makes it reachable by other devices on the LAN, including the power-control API. Do not expose it on untrusted networks. If remote access is required, place it behind an authenticated reverse proxy or authenticated tunnel.

If a Windows Firewall inbound rule is required, run the following command from an elevated PowerShell session:

```powershell
.\Allow_TapoCtrl_WebServer_Firewall.ps1
```

## Local data

Settings, history, and credentials are stored under `%LOCALAPPDATA%\TapoCtrl`. Local application data is excluded from Git.

## Changes in v0.0.82

- Updates the desktop 24-hour graph to use statistic cards consistent with the web version
- Adds dual temperature/humidity axes, a legend, and six statistic cards to environment graphs
- Refreshes open graph history automatically every minute
- Stores history by metric while retaining compatibility with the previous history format
- Refreshes the web dashboard automatically every minute
- Updates the PNG and ICO artwork with a brighter purple design

## Notes

- Device support may vary with the Tapo model, firmware, and versions of `python-kasa` and `tapo`.
- Use power-control and LAN-exposure features only on networks and devices you are authorized to manage.
- This repository currently has no explicit license.
