# EpTUN

- 中文主页: [README-cn.md](README-cn.md)

EpTUN is a Windows GUI/tray app for traffic hijacking and routing based on [tun2socks](https://github.com/xjasonlyu/tun2socks).

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





