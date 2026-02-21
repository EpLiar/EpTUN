# EpTUN 使用文档

- English version: [USAGE.md](USAGE.md)
- 项目主页: [../README-cn.md](../README-cn.md)

本文档重点说明：配置项、名词解释、运行中可重载范围。

## 名词解释

- `TUN`: 虚拟网卡接口，系统路由会把指定流量转发到这里。
- `Wintun`: Windows 下的 TUN 驱动，`tun2socks` 依赖它收发 TUN 流量。
- `上游代理 (upstream proxy)`: `tun2socks` 最终连接的代理端点（如 `socks5://127.0.0.1:10808`）。
- `includeCidrs`: 强制进 TUN 的网段（被 VPN 路由劫持）。
- `excludeCidrs`: 不进 TUN 的网段（从物理网卡默认网关直连）。
- `Bypass CN`: 从 `cn.dat` 读取中国网段并追加到绕行路由。
- `动态排除路由`: 运行期通过 `v2rayA /api/touch` 解析出的排除路由。
- `Route Metric`: 路由优先级，通常数值越小优先级越高。

## 运行中重载规则

VPN 运行期间点击 `Reload Config`（或关闭 Config Editor 后自动触发）时，只会立即应用：

- `logging.windowLevel`
- `logging.fileLevel`
- `logging.trafficSampleMilliseconds`

其他配置项需要 `Restart VPN` 后生效。

## 顶层对象

| 对象 | 作用 |
| --- | --- |
| `proxy` | `tun2socks` 的上游代理地址。 |
| `tun2Socks` | `tun2socks.exe` / `wintun.dll` 路径和启动参数模板。 |
| `vpn` | 网卡、路由、DNS、CN 绕行等设置。 |
| `v2rayA` | v2rayA 登录、端口探测、动态路由集成。 |
| `logging` | 窗口/文件日志等级和流量采样间隔。 |

## `proxy`

| 键 | 类型 | 默认值 | 运行中重载 | 说明 |
| --- | --- | --- | --- | --- |
| `proxy.scheme` | string | `socks5` | 否 | 上游代理协议，仅支持 `socks5`、`http`。 |
| `proxy.host` | string | `127.0.0.1` | 否 | 上游代理主机/IP。 |
| `proxy.port` | int | `10808` | 否 | 上游代理端口（`1..65535`）。 |

## `tun2Socks`

| 键 | 类型 | 默认值 | 运行中重载 | 说明 |
| --- | --- | --- | --- | --- |
| `tun2Socks.executablePath` | string | `tun2socks.exe`（示例配置） | 否 | `tun2socks` 可执行文件路径。 |
| `tun2Socks.wintunDllPath` | string | `wintun.dll` | 否 | `wintun.dll` 源文件路径，启动 `tun2socks` 前会优先从该路径查找。 |
| `tun2Socks.argumentsTemplate` | string | `-device {interfaceName} -proxy {proxyUri} -loglevel info` | 否 | 启动参数模板，支持 `{proxyUri}`、`{interfaceName}` 等占位符。 |

## `vpn`

| 键 | 类型 | 默认值 | 运行中重载 | 说明 |
| --- | --- | --- | --- | --- |
| `vpn.interfaceName` | string | `EpTUN` | 否 | Wintun 网卡名。 |
| `vpn.tunAddress` | IPv4 string | `10.66.66.2` | 否 | TUN 网卡 IPv4 地址。 |
| `vpn.tunGateway` | IPv4 string | `10.66.66.2` | 否 | include 路由使用的网关。 |
| `vpn.tunMask` | IPv4 string | `255.255.255.0` | 否 | TUN 子网掩码。 |
| `vpn.dnsServers` | string[] | `["1.1.1.1","8.8.8.8"]` | 否 | 写入 TUN 网卡的 DNS 列表。 |
| `vpn.includeCidrs` | string[] (CIDR) | `["0.0.0.0/1","128.0.0.0/1"]` | 否 | 进入 TUN 的网段。 |
| `vpn.excludeCidrs` | string[] (CIDR) | 局域网/回环/链路本地等默认网段 | 否 | 绕行 TUN 的网段。 |
| `vpn.cnDatPath` | string | `cn.dat` | 否 | [v2fly/geoip](https://github.com/v2fly/geoip) 的 `cn.dat` 路径。 |
| `vpn.bypassCn` | bool | `false` | 否 | 是否启用 CN 绕行。 |
| `vpn.routeMetric` | int | `6` | 否 | include 路由 metric。 |
| `vpn.startupDelayMs` | int | `1500` | 否 | 启动后等待再配置网卡/路由的毫秒数。 |
| `vpn.defaultGatewayOverride` | string\|null | `null` | 否 | 可选，手工覆盖 IPv4 默认网关。 |
| `vpn.addBypassRouteForProxyHost` | bool | `true` | 否 | 是否为上游代理主机自动加绕行路由。 |

## `v2rayA`

| 键 | 类型 | 默认值 | 运行中重载 | 说明 |
| --- | --- | --- | --- | --- |
| `v2rayA.enabled` | bool | `false` | 否 | 是否启用 v2rayA 集成。 |
| `v2rayA.baseUrl` | string (http/https URL) | `http://localhost:2017` | 否 | v2rayA API 根地址。 |
| `v2rayA.authorization` | string | `""` | 否 | 可直接作为 Authorization 请求头。 |
| `v2rayA.username` | string | `""` | 否 | `POST /api/login` 用户名。 |
| `v2rayA.password` | string | `""` | 否 | `POST /api/login` 密码。 |
| `v2rayA.requestId` | string | `""` | 否 | 可选请求 ID 头。 |
| `v2rayA.timeoutMs` | int | `5000` | 否 | API 超时（`100..120000`）。 |
| `v2rayA.resolveHostnames` | bool | `true` | 否 | 是否将域名节点解析成 IP 并加入动态排除。 |
| `v2rayA.autoDetectProxyPort` | bool | `true` | 否 | 是否通过 `/api/ports` 自动探测端口。 |
| `v2rayA.preferPacPort` | bool | `false` | 否 | 是否优先使用 PAC 端口键。 |
| `v2rayA.proxyHostOverride` | string | `""` | 否 | 生成代理 URI 时可选覆盖 host。 |

当 `v2rayA.enabled = true` 时，鉴权必须满足：

- `authorization` 与 `username+password` 二选一。

## `logging`

| 键 | 类型 | 默认值 | 运行中重载 | 说明 |
| --- | --- | --- | --- | --- |
| `logging.windowLevel` | string | `INFO` | 是 | 窗口日志级别：`INFO`、`WARN`、`ERROR`、`OFF`/`NONE`。 |
| `logging.fileLevel` | string | `INFO` | 是 | 文件日志级别：`INFO`、`WARN`、`ERROR`、`OFF`/`NONE`。 |
| `logging.trafficSampleMilliseconds` | int | `1000` | 是 | 流量速率采样窗口（毫秒，`100..3600000`）。 |

## 外部组件链接

- [tun2socks](https://github.com/xjasonlyu/tun2socks)
- [Wintun](https://www.wintun.net/)
- [v2rayA](https://github.com/v2rayA/v2rayA)
- [v2fly/geoip](https://github.com/v2fly/geoip)
