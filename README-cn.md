# EpTUN

- English README: [README.md](README.md)

EpTUN 提供 Windows 图形界面（无控制台窗口）和托盘图标，用于管理基于 [tun2socks](https://github.com/xjasonlyu/tun2socks) 的路由。

## 外部组件

- [tun2socks](https://github.com/xjasonlyu/tun2socks)
- [Wintun](https://www.wintun.net/)
- [v2rayA](https://github.com/v2rayA/v2rayA)
- [v2fly/geoip](https://github.com/v2fly/geoip)（`cn.dat` 来源）

## 运行（GUI）

- 直接启动 `EpTUN.exe`。
- 程序会自动触发 UAC 提权（`requireAdministrator` manifest）。
- 使用 `Start VPN` / `Stop VPN` / `Restart VPN` 控制会话。
- 使用 `Minimize To Tray` 将程序最小化到系统托盘。
- 运行日志也会写入本地文件（默认在 `appsettings.json` 同目录 `logs/eptun-YYYYMMDD-HHMMSS.log`；失败时回退到 `%LOCALAPPDATA%/EpTUN/logs`）。

## [v2rayA](https://github.com/v2rayA/v2rayA) 集成

当 `v2rayA.enabled = true` 时：

- 如果设置了 `v2rayA.username` + `v2rayA.password`，EpTUN 会先调用 `POST /api/login`，再使用登录后的会话或 token 调后续 API。
- 也可以直接提供 `v2rayA.authorization`，不走登录。
- 鉴权规则：`authorization` 与 `username+password` 二选一。
- 通过 `GET /api/ports` 自动探测本地代理端口。
- SOCKS 模式下默认优先 `socks5`，失败回退到 `socks5WithPac`。
- 通过 `GET /api/touch` 自动将已连接节点 IP 加入运行时 `excludeCidrs`（IPv4 用 `/32`，IPv6 用 `/128`）。
- `v2rayA.baseUrl` 支持 `http://localhost:2017` 和 `http://localhost:2017/`。

## Bypass CN

- 将 `cn.dat`（来自 [v2fly/geoip](https://github.com/v2fly/geoip)）放到输出目录（项目已自动复制该文件）。
- 在 GUI 中勾选 `Bypass CN`。
- 来自 `cn.dat` 的中国 CIDR 会作为绕行路由添加（不走 VPN 劫持）。

相关配置项：

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
- `logging.windowLevel`（`INFO` / `WARN` / `ERROR` / `OFF` 或 `NONE`，默认 `INFO`）
- `logging.fileLevel`（`INFO` / `WARN` / `ERROR` / `OFF` 或 `NONE`，默认 `INFO`）

## 构建

前置要求：

- Windows 上安装 [.NET SDK 10](https://dotnet.microsoft.com/download/dotnet/10.0)（或更高版本）

本地测试构建：

```powershell
dotnet build .\EpTUN.csproj -c Release
```

发布自包含版本（推荐分发）：

```powershell
dotnet publish .\EpTUN.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -o .\bin\EpTUNPortable
```

发布框架依赖版本（体积更小，目标机器需安装运行时）：

```powershell
dotnet publish .\EpTUN.csproj -c Release -r win-x64 --self-contained false `
  -o .\bin\EpTUNCheck
```

发布后请确保 `tun2socks.exe` 与 `wintun.dll`（[Wintun](https://www.wintun.net/)）和 `EpTUN.exe` 位于同一目录。

## 文件说明

- `appsettings.json`：运行配置
- `appsettings.example.json`：配置示例
- `cn.dat`：由 [v2fly/geoip](https://github.com/v2fly/geoip) 生成的中国 CIDR 数据
- `favicon.png`：托盘/窗口图标源文件

## 备注

- 示例配置默认使用与程序同目录的相对路径 `tun2socks.exe` 和 `cn.dat`。
- 请将 `tun2socks.exe` 与 `wintun.dll`（[Wintun](https://www.wintun.net/)）放在 `EpTUN.exe` 同目录。
- 路由与 [`netsh`](https://learn.microsoft.com/windows-server/administration/windows-commands/netsh) 相关操作需要管理员权限。
