# EpTUN Usage Guide

- 中文版: [USAGE-cn.md](USAGE-cn.md)
- Project homepage: [../README.md](../README.md)

This guide focuses on configuration fields, term definitions, and runtime reload behavior.

## Terms

- `TUN`: A virtual network interface used to receive IP packets from the OS routing table.
- `Wintun`: The Windows TUN driver used by `tun2socks`.
- `Upstream proxy`: The local/remote proxy endpoint that `tun2socks` forwards traffic to.
- `includeCidrs`: Routes forced into TUN (hijacked by VPN path).
- `excludeCidrs`: Routes bypassing TUN (go through physical NIC/default gateway).
- `Bypass CN`: Feature that loads CN CIDRs from `cn.dat` and appends them to bypass routes.
- `Dynamic exclude routes`: Runtime excludes discovered from `v2rayA /api/touch`.
- `Route metric`: Priority of route entries (smaller usually wins).

## Runtime Reload Policy

When VPN is running, `Reload Config` (or closing Config Editor) applies only:

- `logging.windowLevel`
- `logging.fileLevel`
- `logging.trafficSampleMilliseconds`

All other fields require `Restart VPN` to take effect.

## Top-Level Objects

| Object | Purpose |
| --- | --- |
| `proxy` | Upstream proxy endpoint for `tun2socks`. |
| `tun2Socks` | `tun2socks.exe` / `wintun.dll` paths and startup arguments template. |
| `vpn` | Interface, routes, DNS, CN bypass, and route behavior. |
| `v2rayA` | v2rayA login/ports/touch integration. |
| `logging` | Window/file log levels and traffic sampling interval. |

## `proxy`

| Key | Type | Default | Runtime reload | Description |
| --- | --- | --- | --- | --- |
| `proxy.scheme` | string | `socks5` | No | Upstream proxy protocol. Allowed: `socks5`, `http`. |
| `proxy.host` | string | `127.0.0.1` | No | Upstream proxy host/IP. |
| `proxy.port` | int | `10808` | No | Upstream proxy port (`1..65535`). |

## `tun2Socks`

| Key | Type | Default | Runtime reload | Description |
| --- | --- | --- | --- | --- |
| `tun2Socks.executablePath` | string | `tun2socks.exe` (example file) | No | Path to `tun2socks` binary. |
| `tun2Socks.wintunDllPath` | string | `wintun.dll` | No | Path to `wintun.dll` source file used before launching `tun2socks`. |
| `tun2Socks.argumentsTemplate` | string | `-device {interfaceName} -proxy {proxyUri} -loglevel info` | No | Launch args template. Supports placeholders like `{proxyUri}` and `{interfaceName}`. |

## `vpn`

| Key | Type | Default | Runtime reload | Description |
| --- | --- | --- | --- | --- |
| `vpn.interfaceName` | string | `EpTUN` | No | Wintun interface name. |
| `vpn.tunAddress` | IPv4 string | `10.66.66.2` | No | TUN interface IPv4 address. |
| `vpn.tunGateway` | IPv4 string | `10.66.66.2` | No | TUN gateway used by include routes. |
| `vpn.tunMask` | IPv4 string | `255.255.255.0` | No | TUN subnet mask. |
| `vpn.dnsServers` | string[] | `["1.1.1.1","8.8.8.8"]` | No | DNS list written to TUN interface. |
| `vpn.includeCidrs` | string[] (CIDR) | `["0.0.0.0/1","128.0.0.0/1"]` | No | CIDRs hijacked into TUN. |
| `vpn.excludeCidrs` | string[] (CIDR) | private/LAN ranges + loopback + link-local defaults | No | CIDRs bypassing TUN. |
| `vpn.cnDatPath` | string | `cn.dat` | No | `cn.dat` path from [v2fly/geoip](https://github.com/v2fly/geoip). |
| `vpn.bypassCn` | bool | `false` | No | Enable CN bypass via `cn.dat`. |
| `vpn.routeMetric` | int | `6` | No | Route metric for include routes. |
| `vpn.startupDelayMs` | int | `1500` | No | Wait time before interface configuration/routing. |
| `vpn.defaultGatewayOverride` | string\|null | `null` | No | Optional manual override of IPv4 default gateway. |
| `vpn.addBypassRouteForProxyHost` | bool | `true` | No | Add bypass routes for upstream proxy host IP(s). |

## `v2rayA`

| Key | Type | Default | Runtime reload | Description |
| --- | --- | --- | --- | --- |
| `v2rayA.enabled` | bool | `false` | No | Enable v2rayA integration. |
| `v2rayA.baseUrl` | string (http/https URL) | `http://localhost:2017` | No | v2rayA API base URL. |
| `v2rayA.authorization` | string | `""` | No | Optional direct Authorization header value. |
| `v2rayA.username` | string | `""` | No | Username for `POST /api/login`. |
| `v2rayA.password` | string | `""` | No | Password for `POST /api/login`. |
| `v2rayA.requestId` | string | `""` | No | Optional request-id header value. |
| `v2rayA.timeoutMs` | int | `5000` | No | API timeout (`100..120000`). |
| `v2rayA.resolveHostnames` | bool | `true` | No | Resolve domain addresses from connected nodes to IP excludes. |
| `v2rayA.autoDetectProxyPort` | bool | `true` | No | Use `/api/ports` to detect proxy port. |
| `v2rayA.preferPacPort` | bool | `false` | No | Prefer PAC-flavored port keys first. |
| `v2rayA.proxyHostOverride` | string | `""` | No | Optional host override when composing proxy URI. |

Auth rule when `v2rayA.enabled = true`:

- Set either `authorization`, or both `username` + `password`.

## `logging`

| Key | Type | Default | Runtime reload | Description |
| --- | --- | --- | --- | --- |
| `logging.windowLevel` | string | `INFO` | Yes | Window log level: `INFO`, `WARN`, `ERROR`, `OFF`/`NONE`. |
| `logging.fileLevel` | string | `INFO` | Yes | File log level: `INFO`, `WARN`, `ERROR`, `OFF`/`NONE`. |
| `logging.trafficSampleMilliseconds` | int | `1000` | Yes | Traffic rate sample window (`100..3600000` ms). |

## External Dependencies

- [tun2socks](https://github.com/xjasonlyu/tun2socks)
- [Wintun](https://www.wintun.net/)
- [v2rayA](https://github.com/v2rayA/v2rayA)
- [v2fly/geoip](https://github.com/v2fly/geoip)
