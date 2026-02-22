# EpTUN

- English homepage: [README.md](README.md)

EpTUN 是一个基于 [tun2socks](https://github.com/xjasonlyu/tun2socks) 的 Windows 图形界面/托盘路由工具。

## Propose（适用场景）

以下场景建议使用 EpTUN：

- 你已有本地代理内核（例如 v2rayA / v2ray-core），并且希望通过 TUN 做系统级流量接管，而不是逐个应用手动配代理。
- 你希望用 GUI/托盘完成启动、停止、重载，而不是手动执行 `netsh` 和路由命令。
- 你需要“分流直连 + 代理转发”的组合策略，例如基于 `cn.dat` 的 CN 直连，其余流量走代理。
- 你需要动态旁路代理节点地址（例如 v2rayA 自动发现的出口地址），避免路由回环和连接风暴。
- 你需要在 Windows 上更可观测、更可回滚的路由操作方式。

如果你只需要浏览器/单应用代理，不需要系统级路由控制，通常不必使用 EpTUN。

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
