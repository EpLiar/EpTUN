# EpTUN

- English homepage: [README.md](README.md)

EpTUN 是一个基于 [tun2socks](https://github.com/xjasonlyu/tun2socks) 的 Windows 图形界面/托盘路由工具。

## 文档入口

- 使用文档（中文）: [docs/USAGE-cn.md](docs/USAGE-cn.md)
- Usage Guide (English): [docs/USAGE.md](docs/USAGE.md)

使用文档重点覆盖：配置项说明、名词解释、运行中热重载范围。

## 依赖组件

- [tun2socks](https://github.com/xjasonlyu/tun2socks)
- [Wintun](https://www.wintun.net/)
- [v2rayA](https://github.com/v2rayA/v2rayA)（可选）
- [v2fly/geoip](https://github.com/v2fly/geoip)（`cn.dat`，可选）

## 快速开始

1. 准备 `appsettings.json`（可参考 `appsettings.example.json`）。
2. 将 `tun2socks.exe`、`wintun.dll` 放到 `EpTUN.exe` 同目录。
3. 以管理员身份运行 `EpTUN.exe`，在 GUI 中操作。

## 构建

```powershell
dotnet build .\EpTUN.csproj -c Release
```

```powershell
dotnet publish .\EpTUN.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -o .\bin\EpTUNPortable
```
