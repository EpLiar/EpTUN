# EpTUN

- 中文文档: [README-cn.md](README-cn.md)

EpTUN provides a Windows GUI (no console window) and tray icon for [tun2socks](https://github.com/xjasonlyu/tun2socks) routing.

## External Components

- [tun2socks](https://github.com/xjasonlyu/tun2socks)
- [Wintun](https://www.wintun.net/)
- [v2rayA](https://github.com/v2rayA/v2rayA)
- [v2ray-core](https://github.com/v2fly/v2ray-core) (optional outbound import source)
- [v2fly/geoip](https://github.com/v2fly/geoip) (`cn.dat` source)

## Run (GUI)

- Launch `EpTUN.exe` directly.
- UAC prompt appears automatically (`requireAdministrator` manifest).
- Use `Start VPN` / `Stop VPN` / `Restart VPN`.
- Use `Minimize To Tray` to keep it in taskbar tray.
- Runtime logs are also written to a local file (default: `logs/eptun-YYYYMMDD-HHMMSS.log` near `appsettings.json`; fallback to `%LOCALAPPDATA%/EpTUN/logs`).

## [v2rayA](https://github.com/v2rayA/v2rayA) integration

When `v2rayA.enabled = true`:

- If `v2rayA.username` + `v2rayA.password` are set, EpTUN calls `POST /api/login` first, then uses the authenticated session/token for subsequent API calls.
- `v2rayA.authorization` can be used directly without login.
- Auth rule: set either `authorization`, or both `username` + `password`.
- `GET /api/ports` is used to detect local proxy port.
- For SOCKS mode, `socks5` is preferred by default, then fallback to `socks5WithPac`.
- `GET /api/touch` is used to auto-add connected node IPs into runtime `excludeCidrs` (`/32` for IPv4, `/128` for IPv6).
- `v2rayA.baseUrl` supports both `http://localhost:2017` and `http://localhost:2017/`.

## Bypass CN

- Put `cn.dat` (from [v2fly/geoip](https://github.com/v2fly/geoip)) in output directory (project now copies it automatically).
- Enable `Bypass CN` checkbox in GUI.
- CN CIDRs from `cn.dat` are added as bypass routes (not hijacked by VPN).

## Import Outbound IPs

- In VPN tab, `excludeCidrs (one per line)` provides `import v2ray config`.
- It reads [v2ray-core](https://github.com/v2fly/v2ray-core) `config.json` `outbounds[*].address`.
- IP/CIDR values are imported directly.
- Domain values are resolved to A/AAAA and imported as `/32` or `/128`.

## Traffic Hijack / Route Model (AsciiDoc)

```adoc
[source,text]
----
[Windows App Traffic]
          |
          v
 [Windows Route Lookup]
          |
          +--> dst in excludeCidrs?
          |      (LAN + CN + proxy host + runtime dynamic excludes)
          |          |
          |          +--> YES -> Physical NIC default gateway (bypass VPN)
          |
          +--> NO  -> 0.0.0.0/1 + 128.0.0.0/1
                         via Wintun (EpTUN)
                               |
                               v
                        tun2socks process
                               |
                               v
                        upstream local proxy
----
```

Config fields:

- `tun2Socks.executablePath`
- `vpn.cnDatPath`
- `vpn.bypassCn`
- `v2rayA.baseUrl`
- `v2rayA.authorization`
- `v2rayA.username`
- `v2rayA.password`
- `v2rayA.autoDetectProxyPort`
- `v2rayA.preferPacPort`
- `v2rayA.proxyHostOverride`
- `logging.windowLevel` (`INFO` / `WARN` / `ERROR` / `OFF` or `NONE`, default `INFO`)
- `logging.fileLevel` (`INFO` / `WARN` / `ERROR` / `OFF` or `NONE`, default `INFO`)
- `logging.trafficSampleMilliseconds` (traffic speed sampling window in milliseconds, `100..3600000`, default `1000`)

## Build

Prerequisite:

- [.NET SDK 10](https://dotnet.microsoft.com/download/dotnet/10.0) (or newer) on Windows

Build for local test:

```powershell
dotnet build .\EpTUN.csproj -c Release
```

Publish self-contained (recommended for sharing):

```powershell
dotnet publish .\EpTUN.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -o .\bin\EpTUNPortable
```

Publish framework-dependent (smaller package, requires target machine runtime):

```powershell
dotnet publish .\EpTUN.csproj -c Release -r win-x64 --self-contained false `
  -o .\bin\EpTUNCheck
```

After publish, ensure `tun2socks.exe` and `wintun.dll` ([Wintun](https://www.wintun.net/)) are in the same directory as `EpTUN.exe`.
## Files

- `appsettings.json`: runtime config
- `appsettings.example.json`: sample config
- `cn.dat`: CN CIDR data generated from [v2fly/geoip](https://github.com/v2fly/geoip)
- `favicon.png`: tray/window icon source

## Notes

- Sample config uses `tun2socks.exe` and `cn.dat` as same-directory relative paths.
- Keep `tun2socks.exe` and `wintun.dll` ([Wintun](https://www.wintun.net/)) in the same folder as `EpTUN.exe`.
- Route and [`netsh`](https://learn.microsoft.com/windows-server/administration/windows-commands/netsh) operations require admin privileges.





