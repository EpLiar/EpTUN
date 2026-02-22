# EpTUN

- 中文主页: [README-cn.md](README-cn.md)

EpTUN is a Windows GUI/tray app for traffic hijacking and routing based on [tun2socks](https://github.com/xjasonlyu/tun2socks).

## Propose

Use EpTUN when you need one or more of these scenarios:

- You have a local proxy core (for example v2rayA/v2ray-core) and want system-level traffic hijack via TUN, not per-app manual proxy setup.
- You want a GUI/tray workflow to start, stop, and reload routing behavior instead of running `netsh` and route commands by hand.
- You need split behavior where selected traffic stays direct (for example CN routes from `cn.dat`) while other traffic goes through proxy.
- You need dynamic bypass for upstream/proxy-node addresses (such as v2rayA discovered endpoints) to avoid routing loops and unstable connections.
- You want runtime observability and safer operations for route-based networking changes on Windows.

EpTUN is usually unnecessary if browser/app-level proxy settings already satisfy your needs and you do not require system-wide routing control.

## Documentation

- Usage Guide (English): [docs/USAGE.md](docs/USAGE.md)
- 使用文档（中文）: [docs/USAGE-cn.md](docs/USAGE-cn.md)

The usage guides focus on configuration fields, term definitions, and runtime reload behavior.

## Dependencies

- [tun2socks](https://github.com/xjasonlyu/tun2socks)
- [Wintun](https://www.wintun.net/)
- [v2rayA](https://github.com/v2rayA/v2rayA) (optional)
- [v2fly/geoip](https://github.com/v2fly/geoip) (`cn.dat`, optional)

## Quick Start

1. Prepare `appsettings.json` (see `appsettings.example.json`).
2. Put `tun2socks.exe` and `wintun.dll` next to `EpTUN.exe`.
3. Run `EpTUN.exe` as administrator and use the GUI.

## Build

```powershell
dotnet build .\EpTUN.csproj -c Release
```

```powershell
dotnet publish .\EpTUN.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -o .\bin\EpTUNPortable
```





