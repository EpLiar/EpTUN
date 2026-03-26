using System.Diagnostics;
using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace EpTUN;

internal sealed class VpnSession
{
    private static readonly TimeSpan SlowOperationLogThreshold = TimeSpan.FromSeconds(1);
    private readonly AppConfig _config;
    private readonly string _configDirectory;
    private readonly bool? _bypassCnOverride;
    private readonly Localizer _i18n;
    private readonly TextWriter _log;
    private readonly TextWriter _error;
    private readonly WindowsRouteManager _routeManager;
    private readonly List<ManagedRoute> _managedRoutes = new();

    private Process? _tun2SocksProcess;
    private IntPtr _tun2SocksJobHandle;

    public VpnSession(
        AppConfig config,
        string configPath,
        TextWriter log,
        TextWriter error,
        Localizer i18n,
        bool? bypassCnOverride = null)
    {
        _config = config;
        _configDirectory = Path.GetDirectoryName(Path.GetFullPath(configPath))
            ?? AppContext.BaseDirectory;
        _bypassCnOverride = bypassCnOverride;
        _i18n = i18n;
        _log = log;
        _error = error;
        _routeManager = new WindowsRouteManager(log, error, i18n);
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var defaultRoute = await MeasureAsync(
            () => ResolveDefaultRouteAsync(cancellationToken),
            "IPv4 默认路由解析",
            "IPv4 default route resolution");
        var proxyUri = await MeasureAsync(
            () => ResolveProxyUriAsync(cancellationToken),
            "代理端点解析",
            "Proxy endpoint resolution");
        await MeasureAsync(
            () => EnsureProxyEndpointReachableAsync(proxyUri, cancellationToken),
            "代理端点连通性检查",
            "Proxy endpoint reachability check");
        var proxyHosts = await MeasureAsync(
            () => ResolveProxyHostsAsync(proxyUri, cancellationToken),
            "代理主机解析",
            "Proxy host resolution");
        var dynamicExcludeRoutes = await MeasureAsync(
            () => ResolveDynamicExcludeRoutesAsync(cancellationToken),
            "动态排除路由解析",
            "Dynamic exclude route resolution");
        var cnExcludeRoutes = Measure(
            ResolveCnExcludeRoutes,
            "CN 路由加载",
            "CN route loading");
        var shouldResolveDefaultRouteV6 = ShouldResolveDefaultRouteV6(proxyHosts, dynamicExcludeRoutes, cnExcludeRoutes);
        var defaultRouteV6 = shouldResolveDefaultRouteV6
            ? await MeasureAsync(
                () => ResolveDefaultRouteV6Async(cancellationToken),
                "IPv6 默认路由解析",
                "IPv6 default route resolution")
            : null;

        _log.WriteLine(T($"[INFO] 代理端点：{proxyUri}", $"[INFO] Proxy endpoint: {proxyUri}"));
        _log.WriteLine(T($"[INFO] VPN 启动前的默认网关：{defaultRoute.Gateway}", $"[INFO] Default gateway before VPN: {defaultRoute.Gateway}"));
        if (defaultRouteV6 is not null)
        {
            _log.WriteLine(
                T(
                    $"[INFO] VPN 启动前的 IPv6 默认网关：{defaultRouteV6.Gateway}（接口 {defaultRouteV6.InterfaceIndex}）",
                    $"[INFO] IPv6 default gateway before VPN: {defaultRouteV6.Gateway} (IF {defaultRouteV6.InterfaceIndex})"));
        }
        else if (shouldResolveDefaultRouteV6)
        {
            _log.WriteLine(T("[INFO] 未找到 IPv6 默认路由，将跳过 IPv6 绕过路由。", "[INFO] IPv6 default route not found. IPv6 bypass routes will be skipped."));
        }

        _tun2SocksProcess = StartTun2Socks(proxyUri);
        using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var stdoutPump = PumpAsync(_tun2SocksProcess.StandardOutput, _log, "[tun2socks] ", pumpCts.Token);
        var stderrPump = PumpAsync(_tun2SocksProcess.StandardError, _log, "[tun2socks] ", pumpCts.Token);

        try
        {
            if (_config.Vpn.StartupDelayMs > 0)
            {
                await Task.Delay(_config.Vpn.StartupDelayMs, cancellationToken);
            }

            if (_tun2SocksProcess.HasExited)
            {
                throw new InvalidOperationException(
                    T(
                        $"tun2socks 提前退出，退出码 {_tun2SocksProcess.ExitCode}。请检查前面的 [tun2socks] 日志。",
                        $"tun2socks exited early with code {_tun2SocksProcess.ExitCode}. Check previous [tun2socks] logs for details."));
            }

            await EnsureTunInterfaceConfiguredAsync(cancellationToken);
            var tunInterfaceIndex = await _routeManager.GetInterfaceIndexByNameAsync(_config.Vpn.InterfaceName, cancellationToken);
            _log.WriteLine(T($"[INFO] TUN 接口索引：{tunInterfaceIndex}", $"[INFO] TUN interface index: {tunInterfaceIndex}"));
            await ApplyRoutesAsync(
                defaultRoute.Gateway,
                defaultRouteV6,
                proxyHosts,
                tunInterfaceIndex,
                dynamicExcludeRoutes,
                cnExcludeRoutes,
                cancellationToken);

            _log.WriteLine(T("[INFO] VPN 路由已应用。可在主窗口停止 VPN。", "[INFO] VPN routes applied. Stop the VPN from the main window."));

            var exitTask = _tun2SocksProcess.WaitForExitAsync(CancellationToken.None);
            var cancelTask = Task.Delay(Timeout.Infinite, cancellationToken);

            var completed = await Task.WhenAny(exitTask, cancelTask);
            if (completed == exitTask && !cancellationToken.IsCancellationRequested)
            {
                throw new InvalidOperationException(
                    T(
                        $"tun2socks 异常退出，退出码 {_tun2SocksProcess.ExitCode}。",
                        $"tun2socks exited unexpectedly with code {_tun2SocksProcess.ExitCode}."));
            }
        }
        finally
        {
            pumpCts.Cancel();
            await CleanupRoutesAsync(CancellationToken.None);
            StopTun2Socks();

            await IgnoreFailuresAsync(stdoutPump);
            await IgnoreFailuresAsync(stderrPump);
        }
    }

    private bool ShouldResolveDefaultRouteV6(
        IReadOnlyCollection<IPAddress> proxyHosts,
        IReadOnlyCollection<CidrRoute> dynamicExcludeRoutes,
        IReadOnlyCollection<CidrRoute> cnExcludeRoutes)
    {
        var includeRoutes = _config.Vpn.IncludeCidrs
            .Select(CidrRoute.Parse)
            .Distinct()
            .ToArray();
        if (!includeRoutes.Any(static route => route.IsIPv6))
        {
            return false;
        }

        if (_config.Vpn.ExcludeCidrs.Select(CidrRoute.Parse).Any(static route => route.IsIPv6))
        {
            return true;
        }

        if (dynamicExcludeRoutes.Any(static route => route.IsIPv6) ||
            cnExcludeRoutes.Any(static route => route.IsIPv6))
        {
            return true;
        }

        if (!_config.Vpn.AddBypassRouteForProxyHost)
        {
            return false;
        }

        return proxyHosts.Any(static host =>
            host.AddressFamily == AddressFamily.InterNetworkV6 &&
            !IPAddress.IsLoopback(host));
    }

    private async Task<DefaultRoute> ResolveDefaultRouteAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_config.Vpn.DefaultGatewayOverride))
        {
            var gateway = IPAddress.Parse(_config.Vpn.DefaultGatewayOverride);
            return new DefaultRoute(gateway, IPAddress.Any, 0);
        }

        return await _routeManager.GetDefaultRouteAsync(cancellationToken);
    }

    private async Task<DefaultRouteV6?> ResolveDefaultRouteV6Async(CancellationToken cancellationToken)
    {
        try
        {
            return await _routeManager.GetDefaultRouteV6Async(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _error.WriteLine(T($"[WARN] 解析 IPv6 默认路由失败：{ex.Message}", $"[WARN] Failed to resolve IPv6 default route: {ex.Message}"));
            return null;
        }
    }

    private async Task<Uri> ResolveProxyUriAsync(CancellationToken cancellationToken)
    {
        var fallback = _config.Proxy.BuildUri();

        if (!_config.V2RayA.Enabled || !_config.V2RayA.AutoDetectProxyPort)
        {
            return fallback;
        }

        try
        {
            return await V2RayATouchClient.ResolveProxyUriAsync(_config.V2RayA, _config.Proxy, _log, _i18n, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _error.WriteLine(
                T(
                    $"[WARN] v2rayA /api/ports 调用失败，将回退到 proxy 配置：{ex.Message}",
                    $"[WARN] v2rayA /api/ports failed, fallback to proxy config: {ex.Message}"));
            return fallback;
        }
    }

    private static async Task<IReadOnlyCollection<IPAddress>> ResolveProxyHostsAsync(Uri proxyUri, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(proxyUri.Host, out var ip))
        {
            return [ip];
        }

        var addresses = await Dns.GetHostAddressesAsync(proxyUri.Host, cancellationToken);
        return addresses
            .Where(static x => x.AddressFamily == AddressFamily.InterNetwork || x.AddressFamily == AddressFamily.InterNetworkV6)
            .Distinct()
            .ToArray();
    }

    private async Task EnsureProxyEndpointReachableAsync(Uri proxyUri, CancellationToken cancellationToken)
    {
        try
        {
            using var tcpClient = new TcpClient();
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linkedCts.CancelAfter(2000);
            await tcpClient.ConnectAsync(proxyUri.Host, proxyUri.Port, linkedCts.Token);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                T(
                    $"代理端点不可达：{proxyUri}。请检查本地代理服务和端口。{ex.Message}",
                    $"Proxy endpoint is not reachable: {proxyUri}. Check local proxy service and port. {ex.Message}"));
        }
    }

    private async Task<IReadOnlyCollection<CidrRoute>> ResolveDynamicExcludeRoutesAsync(CancellationToken cancellationToken)
    {
        if (!_config.V2RayA.Enabled)
        {
            return [];
        }

        try
        {
            return await V2RayATouchClient.ResolveExcludeCidrsAsync(_config.V2RayA, _log, _i18n, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _error.WriteLine(T($"[WARN] v2rayA 自动 excludeCidrs 失败：{ex.Message}", $"[WARN] v2rayA auto excludeCidrs failed: {ex.Message}"));
            return [];
        }
    }

    private IReadOnlyCollection<CidrRoute> ResolveCnExcludeRoutes()
    {
        if (!ResolveBypassCnEnabled())
        {
            return [];
        }

        var cnDatPath = ResolveCnDatPath();
        if (!File.Exists(cnDatPath))
        {
            _error.WriteLine(T($"[WARN] 未找到 CN dat 文件：{cnDatPath}", $"[WARN] CN dat file not found: {cnDatPath}"));
            return [];
        }

        try
        {
            var routes = GeoIpCnDatProvider.LoadCnCidrs(cnDatPath);
            var ipv4Count = routes.Count(static x => x.IsIPv4);
            var ipv6Count = routes.Count - ipv4Count;
            _log.WriteLine(
                T(
                    $"[INFO] Bypass CN：已从 {cnDatPath} 加载 {ipv4Count} 条 IPv4 和 {ipv6Count} 条 IPv6 CIDR。",
                    $"[INFO] Bypass CN: loaded {ipv4Count} IPv4 + {ipv6Count} IPv6 CIDRs from {cnDatPath}"));
            return routes;
        }
        catch (Exception ex)
        {
            _error.WriteLine(T($"[WARN] 加载 CN 路由失败：{ex.Message}", $"[WARN] Failed to load CN routes: {ex.Message}"));
            return [];
        }
    }

    private bool ResolveBypassCnEnabled()
    {
        if (_bypassCnOverride.HasValue)
        {
            return _bypassCnOverride.Value;
        }

        return _config.Vpn.BypassCn;
    }

    private string ResolveCnDatPath()
    {
        var configured = _config.Vpn.CnDatPath.Trim();
        if (Path.IsPathRooted(configured))
        {
            return Path.GetFullPath(configured);
        }

        var candidates = new[]
        {
            Path.GetFullPath(Path.Combine(_configDirectory, configured)),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, configured)),
            Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, configured)),
            Path.GetFullPath(Path.Combine(_configDirectory, Path.GetFileName(configured))),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, Path.GetFileName(configured)))
        };

        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }
    private async Task EnsureTunInterfaceConfiguredAsync(CancellationToken cancellationToken)
    {
        const int maxAttempts = 12;
        Exception? lastError = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await ConfigureTunAddressAsync(cancellationToken);
                await ConfigureTunDnsAsync(cancellationToken);

                _log.WriteLine(
                    T(
                        $"[INFO] TUN 接口“{_config.Vpn.InterfaceName}”已配置：{_config.Vpn.TunAddress}/{_config.Vpn.TunMask}",
                        $"[INFO] TUN interface '{_config.Vpn.InterfaceName}' configured: {_config.Vpn.TunAddress}/{_config.Vpn.TunMask}"));
                return;
            }
            catch (Exception ex)
            {
                lastError = ex;
                if (attempt < maxAttempts)
                {
                    await Task.Delay(500, cancellationToken);
                }
            }
        }

        throw new InvalidOperationException(
            T(
                $"配置 TUN 接口“{_config.Vpn.InterfaceName}”失败。请检查 appsettings.json 里的 vpn.interfaceName 和 tun2socks 设备名。",
                $"Failed to configure TUN interface '{_config.Vpn.InterfaceName}'. Check vpn.interfaceName and tun2socks device name in appsettings.json."),
            lastError);
    }

    private async Task ConfigureTunAddressAsync(CancellationToken cancellationToken)
    {
        var args =
            $"interface ipv4 set address name={QuoteArg(_config.Vpn.InterfaceName)} source=static addr={_config.Vpn.TunAddress} mask={_config.Vpn.TunMask}";

        var result = await RunCommandAsync("netsh", args, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                T(
                    $"设置 TUN 地址失败。命令：netsh {args}\n{result.Stdout}\n{result.Stderr}".Trim(),
                    $"Failed to set TUN address. netsh {args}\n{result.Stdout}\n{result.Stderr}".Trim()));
        }
    }

    private async Task ConfigureTunDnsAsync(CancellationToken cancellationToken)
    {
        if (_config.Vpn.DnsServers.Length == 0)
        {
            return;
        }

        var primary = _config.Vpn.DnsServers[0];
        var setPrimaryArgs =
            $"interface ipv4 set dnsservers name={QuoteArg(_config.Vpn.InterfaceName)} source=static address={primary} register=none validate=no";
        var primaryResult = await RunCommandAsync("netsh", setPrimaryArgs, cancellationToken);

        if (primaryResult.ExitCode != 0)
        {
            throw new InvalidOperationException(
                T(
                    $"设置主 DNS 失败。命令：netsh {setPrimaryArgs}\n{primaryResult.Stdout}\n{primaryResult.Stderr}".Trim(),
                    $"Failed to set primary DNS. netsh {setPrimaryArgs}\n{primaryResult.Stdout}\n{primaryResult.Stderr}".Trim()));
        }

        for (var i = 1; i < _config.Vpn.DnsServers.Length; i++)
        {
            var dns = _config.Vpn.DnsServers[i];
            var addArgs =
                $"interface ipv4 add dnsservers name={QuoteArg(_config.Vpn.InterfaceName)} address={dns} index={i + 1} validate=no";
            var addResult = await RunCommandAsync("netsh", addArgs, cancellationToken);

            if (addResult.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    T(
                        $"添加 DNS 服务器 {dns} 失败。命令：netsh {addArgs}\n{addResult.Stdout}\n{addResult.Stderr}".Trim(),
                        $"Failed to add DNS server {dns}. netsh {addArgs}\n{addResult.Stdout}\n{addResult.Stderr}".Trim()));
            }
        }
    }

    private async Task ApplyRoutesAsync(
        IPAddress originalGateway,
        DefaultRouteV6? defaultRouteV6,
        IReadOnlyCollection<IPAddress> proxyHosts,
        int tunInterfaceIndex,
        IReadOnlyCollection<CidrRoute> dynamicExcludeRoutes,
        IReadOnlyCollection<CidrRoute> cnExcludeRoutes,
        CancellationToken cancellationToken)
    {
        var vpnGateway = IPAddress.Parse(_config.Vpn.TunGateway);
        var includeRoutes = _config.Vpn.IncludeCidrs
            .Select(CidrRoute.Parse)
            .Distinct()
            .ToArray();
        var hasIpv6Include = includeRoutes.Any(static x => x.IsIPv6);

        var baseExcludeRoutes = _config.Vpn.ExcludeCidrs
            .Select(CidrRoute.Parse)
            .ToArray();

        IReadOnlyCollection<CidrRoute> effectiveDynamicExcludeRoutes = dynamicExcludeRoutes;
        IReadOnlyCollection<CidrRoute> effectiveCnExcludeRoutes = cnExcludeRoutes;
        IReadOnlyCollection<CidrRoute> effectiveBaseExcludeRoutes = baseExcludeRoutes;

        if (!hasIpv6Include)
        {
            var skippedBaseV6 = baseExcludeRoutes.Count(static x => x.IsIPv6);
            var skippedDynamicV6 = dynamicExcludeRoutes.Count(static x => x.IsIPv6);
            var skippedCnV6 = cnExcludeRoutes.Count(static x => x.IsIPv6);
            var skippedTotalV6 = skippedBaseV6 + skippedDynamicV6 + skippedCnV6;

            if (skippedTotalV6 > 0)
            {
                _log.WriteLine(
                    T(
                        $"[INFO] 未配置 IPv6 包含路由，已跳过 {skippedTotalV6} 条 IPv6 排除路由（静态 {skippedBaseV6}、动态 {skippedDynamicV6}、CN {skippedCnV6}）。",
                        $"[INFO] IPv6 include routes are not configured; skipped {skippedTotalV6} IPv6 exclude routes ({skippedBaseV6} static, {skippedDynamicV6} dynamic, {skippedCnV6} CN)."));
            }

            effectiveBaseExcludeRoutes = baseExcludeRoutes.Where(static x => x.IsIPv4).ToArray();
            effectiveDynamicExcludeRoutes = dynamicExcludeRoutes.Where(static x => x.IsIPv4).ToArray();
            effectiveCnExcludeRoutes = cnExcludeRoutes.Where(static x => x.IsIPv4).ToArray();
        }

        if (_config.Vpn.AddBypassRouteForProxyHost)
        {
            var proxyRoutes = new HashSet<CidrRoute>();
            foreach (var host in proxyHosts)
            {
                if (IPAddress.IsLoopback(host))
                {
                    continue;
                }

                if (host.AddressFamily == AddressFamily.InterNetwork)
                {
                    proxyRoutes.Add(CidrRoute.Parse($"{host}/32"));
                }
                else if (host.AddressFamily == AddressFamily.InterNetworkV6)
                {
                    proxyRoutes.Add(CidrRoute.Parse($"{host}/128"));
                }
            }

            var skippedProxyIpv6NoInclude = 0;
            var skippedProxyIpv6NoDefaultRoute = 0;
            foreach (var proxyRoute in proxyRoutes)
            {
                if (proxyRoute.IsIPv4)
                {
                    await AddManagedRouteAsync(proxyRoute, originalGateway, 1, cancellationToken);
                }
                else if (!hasIpv6Include)
                {
                    skippedProxyIpv6NoInclude++;
                }
                else if (defaultRouteV6 is not null)
                {
                    await AddManagedRouteAsync(
                        proxyRoute,
                        defaultRouteV6.Gateway,
                        1,
                        cancellationToken,
                        defaultRouteV6.InterfaceIndex);
                }
                else
                {
                    skippedProxyIpv6NoDefaultRoute++;
                }
            }

            if (skippedProxyIpv6NoInclude > 0)
            {
                _log.WriteLine(
                    T(
                        $"[INFO] 由于未配置 IPv6 包含路由，已跳过 {skippedProxyIpv6NoInclude} 条 IPv6 代理绕过路由。",
                        $"[INFO] Skipped {skippedProxyIpv6NoInclude} IPv6 proxy bypass routes because IPv6 include routes are not configured."));
            }

            if (skippedProxyIpv6NoDefaultRoute > 0)
            {
                _error.WriteLine(
                    T(
                        $"[WARN] 已跳过 {skippedProxyIpv6NoDefaultRoute} 条 IPv6 代理绕过路由：没有 IPv6 默认路由。",
                        $"[WARN] Skipped {skippedProxyIpv6NoDefaultRoute} IPv6 proxy bypass routes: no IPv6 default route."));
            }
        }

        var excludeMetric = Math.Max(1, _config.Vpn.RouteMetric - 1);
        var allExcludeRoutes = effectiveBaseExcludeRoutes
            .Concat(effectiveDynamicExcludeRoutes)
            .Concat(effectiveCnExcludeRoutes)
            .Distinct()
            .ToArray();

        if (effectiveDynamicExcludeRoutes.Count > 0)
        {
            var dynamicV4 = effectiveDynamicExcludeRoutes.Count(static x => x.IsIPv4);
            var dynamicV6 = effectiveDynamicExcludeRoutes.Count - dynamicV4;
            _log.WriteLine(
                T(
                    $"[INFO] 已从 v2rayA 添加 {effectiveDynamicExcludeRoutes.Count} 条动态排除路由（IPv4 {dynamicV4}、IPv6 {dynamicV6}）。",
                    $"[INFO] Added {effectiveDynamicExcludeRoutes.Count} dynamic exclude routes from v2rayA ({dynamicV4} IPv4, {dynamicV6} IPv6)."));
        }

        if (effectiveCnExcludeRoutes.Count > 0)
        {
            var cnV4 = effectiveCnExcludeRoutes.Count(static x => x.IsIPv4);
            var cnV6 = effectiveCnExcludeRoutes.Count - cnV4;
            _log.WriteLine(
                T(
                    $"[INFO] 已添加 {effectiveCnExcludeRoutes.Count} 条 CN 排除路由（IPv4 {cnV4}、IPv6 {cnV6}）。",
                    $"[INFO] Added {effectiveCnExcludeRoutes.Count} CN exclude routes ({cnV4} IPv4, {cnV6} IPv6)."));
        }

        var verboseExcludeLogs = allExcludeRoutes.Length <= 200;
        if (!verboseExcludeLogs)
        {
            _log.WriteLine(
                T(
                    $"[INFO] 正在应用 {allExcludeRoutes.Length} 条排除路由（单条路由日志已抑制）。",
                    $"[INFO] Applying {allExcludeRoutes.Length} exclude routes (route-level logs suppressed)."));
        }

        var excludeRouteApplyStopwatch = Stopwatch.StartNew();
        var skippedIpv6Exclude = 0;
        for (var i = 0; i < allExcludeRoutes.Length; i++)
        {
            var cidr = allExcludeRoutes[i];
            if (cidr.IsIPv4)
            {
                await AddManagedRouteAsync(cidr, originalGateway, excludeMetric, cancellationToken, logRoute: verboseExcludeLogs);
            }
            else if (defaultRouteV6 is null)
            {
                skippedIpv6Exclude++;
            }
            else
            {
                await AddManagedRouteAsync(
                    cidr,
                    defaultRouteV6.Gateway,
                    excludeMetric,
                    cancellationToken,
                    defaultRouteV6.InterfaceIndex,
                    logRoute: verboseExcludeLogs);
            }

            if (!verboseExcludeLogs && ((i + 1) % 500 == 0 || i == allExcludeRoutes.Length - 1))
            {
                _log.WriteLine(T($"[INFO] 已应用 {i + 1}/{allExcludeRoutes.Length} 条排除路由...", $"[INFO] Applied {i + 1}/{allExcludeRoutes.Length} exclude routes..."));
            }
        }

        excludeRouteApplyStopwatch.Stop();
        if (allExcludeRoutes.Length > 0)
        {
            _log.WriteLine(
                T(
                    $"[INFO] 排除路由应用完成：共 {allExcludeRoutes.Length} 条，耗时 {excludeRouteApplyStopwatch.Elapsed.TotalSeconds:F1}s。",
                    $"[INFO] Exclude routes apply completed in {excludeRouteApplyStopwatch.Elapsed.TotalSeconds:F1}s for {allExcludeRoutes.Length} routes."));
        }

        if (skippedIpv6Exclude > 0)
        {
            _error.WriteLine(T($"[WARN] 已跳过 {skippedIpv6Exclude} 条 IPv6 排除路由：没有 IPv6 默认路由。", $"[WARN] Skipped {skippedIpv6Exclude} IPv6 exclude routes: no IPv6 default route."));
        }
        // Enable global hijack after bypass routes are in place to avoid startup race conditions.
        foreach (var cidr in includeRoutes)
        {
            if (cidr.IsIPv4)
            {
                await AddManagedRouteAsync(cidr, vpnGateway, _config.Vpn.RouteMetric, cancellationToken, tunInterfaceIndex);
            }
            else
            {
                await AddManagedRouteAsync(cidr, gateway: null, _config.Vpn.RouteMetric, cancellationToken, tunInterfaceIndex);
            }
        }

        _log.WriteLine(T($"[INFO] 核心包含路由已应用（{includeRoutes.Length} 条）。", $"[INFO] Core include routes applied ({includeRoutes.Length})."));
    }
    private async Task AddManagedRouteAsync(
        CidrRoute route,
        IPAddress? gateway,
        int metric,
        CancellationToken cancellationToken,
        int? interfaceIndex = null,
        bool logRoute = true)
    {
        await _routeManager.AddRouteAsync(
            route,
            gateway,
            metric,
            cancellationToken,
            interfaceIndex,
            logCommand: logRoute,
            replaceIfExists: true);
        _managedRoutes.Add(new ManagedRoute(route, gateway, interfaceIndex));

        if (logRoute)
        {
            var nextHop = gateway is null
                ? $"on-link IF {interfaceIndex?.ToString() ?? "?"}"
                : gateway.ToString();
            _log.WriteLine(
                T(
                    $"[INFO] 路由 {route.Network}/{route.PrefixLength} -> {nextHop}（metric {metric}）",
                    $"[INFO] Route {route.Network}/{route.PrefixLength} -> {nextHop} (metric {metric})"));
        }
    }

    private async Task CleanupRoutesAsync(CancellationToken cancellationToken)
    {
        for (var i = _managedRoutes.Count - 1; i >= 0; i--)
        {
            var route = _managedRoutes[i];
            await _routeManager.DeleteRouteAsync(
                route.Route,
                route.Gateway,
                cancellationToken,
                suppressWarning: false,
                route.InterfaceIndex);
        }

        _managedRoutes.Clear();
    }

    private Process StartTun2Socks(Uri proxyUri)
    {
        var exePath = ResolveTun2SocksExecutablePath();
        if (!File.Exists(exePath))
        {
            throw new FileNotFoundException(T($"未找到 tun2socks 可执行文件：{exePath}", $"tun2socks executable not found: {exePath}"));
        }

        EnsureWintunDllAvailable(exePath);

        var arguments = BuildTun2SocksArgs(proxyUri);
        _log.WriteLine(T($"[INFO] 正在启动 tun2socks：{exePath} {arguments}", $"[INFO] Starting tun2socks: {exePath} {arguments}"));

        var workingDirectory = Path.GetDirectoryName(exePath)
            ?? AppContext.BaseDirectory;

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        if (!process.Start())
        {
            throw new InvalidOperationException(T("启动 tun2socks 失败。", "Failed to start tun2socks."));
        }

        TryAttachTun2SocksToJobObject(process);
        return process;
    }

    private string ResolveTun2SocksExecutablePath()
    {
        var configured = _config.Tun2Socks.ExecutablePath.Trim();
        if (Path.IsPathRooted(configured))
        {
            return Path.GetFullPath(configured);
        }

        var candidates = new[]
        {
            Path.GetFullPath(Path.Combine(_configDirectory, configured)),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, configured)),
            Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, configured)),
            Path.GetFullPath(Path.Combine(_configDirectory, Path.GetFileName(configured))),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, Path.GetFileName(configured)))
        };

        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }

    private void EnsureWintunDllAvailable(string tun2SocksExePath)
    {
        var tun2SocksDirectory = Path.GetDirectoryName(tun2SocksExePath)
            ?? AppContext.BaseDirectory;
        var targetPath = Path.Combine(tun2SocksDirectory, "wintun.dll");

        if (File.Exists(targetPath))
        {
            return;
        }

        var candidates = ResolveWintunDllCandidates()
        .Select(Path.GetFullPath)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Where(File.Exists)
        .ToArray();

        if (candidates.Length > 0)
        {
            File.Copy(candidates[0], targetPath, overwrite: false);
            _log.WriteLine(T($"[INFO] 已复制 wintun.dll 到：{targetPath}", $"[INFO] Copied wintun.dll to: {targetPath}"));
            return;
        }

        var configuredPath = _config.Tun2Socks.WintunDllPath.Trim();
        throw new FileNotFoundException(
            T(
                $"未找到 wintun.dll。已检查 tun2Socks.wintunDllPath='{configuredPath}' 及回退路径。请将 x64 的 wintun.dll 放到 tun2socks.exe 同目录，或更新 tun2Socks.wintunDllPath。",
                $"wintun.dll not found. Checked tun2Socks.wintunDllPath='{configuredPath}' and fallback paths. Put x64 wintun.dll in the same directory as tun2socks.exe or update tun2Socks.wintunDllPath."),
            targetPath);
    }

    private IEnumerable<string> ResolveWintunDllCandidates()
    {
        var candidates = new List<string>();
        var configured = _config.Tun2Socks.WintunDllPath.Trim();

        if (Path.IsPathRooted(configured))
        {
            candidates.Add(configured);
        }
        else
        {
            candidates.Add(Path.Combine(_configDirectory, configured));
            candidates.Add(Path.Combine(AppContext.BaseDirectory, configured));
            candidates.Add(Path.Combine(Environment.CurrentDirectory, configured));
            candidates.Add(Path.Combine(_configDirectory, Path.GetFileName(configured)));
            candidates.Add(Path.Combine(AppContext.BaseDirectory, Path.GetFileName(configured)));
        }

        candidates.Add(Path.Combine(_configDirectory, "wintun.dll"));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "wintun.dll"));
        candidates.Add(Path.Combine(Environment.CurrentDirectory, "wintun.dll"));
        candidates.Add(Path.Combine(_configDirectory, "bin", "wintun.dll"));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "bin", "wintun.dll"));

        return candidates;
    }

    private string BuildTun2SocksArgs(Uri proxyUri)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["proxyUri"] = proxyUri.ToString(),
            ["interfaceName"] = _config.Vpn.InterfaceName,
            ["tunAddress"] = _config.Vpn.TunAddress,
            ["tunGateway"] = _config.Vpn.TunGateway,
            ["tunMask"] = _config.Vpn.TunMask,
            ["dnsServers"] = string.Join(",", _config.Vpn.DnsServers)
        };

        var args = _config.Tun2Socks.ArgumentsTemplate;
        foreach (var item in values)
        {
            args = args.Replace("{" + item.Key + "}", item.Value, StringComparison.OrdinalIgnoreCase);
        }

        return args;
    }

    private void StopTun2Socks()
    {
        try
        {
            if (_tun2SocksProcess is not null && !_tun2SocksProcess.HasExited)
            {
                _tun2SocksProcess.Kill(entireProcessTree: true);
                _tun2SocksProcess.WaitForExit(2000);
            }
        }
        catch (Exception ex)
        {
            _error.WriteLine(T($"[WARN] 停止 tun2socks 失败：{ex.Message}", $"[WARN] Failed to stop tun2socks: {ex.Message}"));
        }

        _tun2SocksProcess?.Dispose();
        _tun2SocksProcess = null;
        CloseTun2SocksJobHandle();
    }

    private void TryAttachTun2SocksToJobObject(Process process)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var jobHandle = CreateJobObject(IntPtr.Zero, null);
        if (jobHandle == IntPtr.Zero)
        {
            _error.WriteLine(T("[WARN] 无法创建 tun2socks Job Object；主程序异常退出时可能会遗留子进程。", "[WARN] Failed to create a Job Object for tun2socks; the child process may survive an EpTUN crash."));
            return;
        }

        var info = new JobObjectExtendedLimitInformation();
        info.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
        var infoSize = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
        var infoPtr = Marshal.AllocHGlobal(infoSize);
        try
        {
            Marshal.StructureToPtr(info, infoPtr, false);
            if (!SetInformationJobObject(jobHandle, JobObjectExtendedLimitInformationClass, infoPtr, (uint)infoSize))
            {
                var message = new Win32Exception(Marshal.GetLastWin32Error()).Message;
                _error.WriteLine(T($"[WARN] 配置 tun2socks Job Object 失败：{message}", $"[WARN] Failed to configure the tun2socks Job Object: {message}"));
                _ = CloseHandle(jobHandle);
                return;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(infoPtr);
        }

        if (!AssignProcessToJobObject(jobHandle, process.Handle))
        {
            var message = new Win32Exception(Marshal.GetLastWin32Error()).Message;
            _error.WriteLine(T($"[WARN] 无法将 tun2socks 绑定到 Job Object：{message}", $"[WARN] Failed to assign tun2socks to the Job Object: {message}"));
            _ = CloseHandle(jobHandle);
            return;
        }

        _tun2SocksJobHandle = jobHandle;
    }

    private void CloseTun2SocksJobHandle()
    {
        if (_tun2SocksJobHandle == IntPtr.Zero)
        {
            return;
        }

        _ = CloseHandle(_tun2SocksJobHandle);
        _tun2SocksJobHandle = IntPtr.Zero;
    }

    private async Task<T> MeasureAsync<T>(Func<Task<T>> action, string chineseLabel, string englishLabel)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = await action();
        stopwatch.Stop();
        LogSlowOperation(stopwatch.Elapsed, chineseLabel, englishLabel);
        return result;
    }

    private async Task MeasureAsync(Func<Task> action, string chineseLabel, string englishLabel)
    {
        var stopwatch = Stopwatch.StartNew();
        await action();
        stopwatch.Stop();
        LogSlowOperation(stopwatch.Elapsed, chineseLabel, englishLabel);
    }

    private T Measure<T>(Func<T> action, string chineseLabel, string englishLabel)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = action();
        stopwatch.Stop();
        LogSlowOperation(stopwatch.Elapsed, chineseLabel, englishLabel);
        return result;
    }

    private void LogSlowOperation(TimeSpan elapsed, string chineseLabel, string englishLabel)
    {
        if (elapsed < SlowOperationLogThreshold)
        {
            return;
        }

        _log.WriteLine(
            T(
                $"[INFO] {chineseLabel}耗时 {elapsed.TotalSeconds:F1}s。",
                $"[INFO] {englishLabel} took {elapsed.TotalSeconds:F1}s."));
    }

    private static string QuoteArg(string value)
    {
        return $"\"{value.Replace("\"", string.Empty)}\"";
    }

    private async Task<CommandResult> RunCommandAsync(
        string fileName,
        string arguments,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        if (!process.Start())
        {
            throw new InvalidOperationException(T($"启动进程失败：{fileName}", $"Failed to start process: {fileName}"));
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);

        return new CommandResult(process.ExitCode, await stdoutTask, await stderrTask);
    }

    private static async Task PumpAsync(TextReader reader, TextWriter writer, string prefix, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                break;
            }


            writer.WriteLine(prefix + line);
        }
    }
    private static async Task IgnoreFailuresAsync(Task task)
    {
        try
        {
            await task;
        }
        catch
        {
            // Ignore background I/O cancellation errors during shutdown.
        }
    }

    private sealed record ManagedRoute(CidrRoute Route, IPAddress? Gateway, int? InterfaceIndex);

    private sealed record CommandResult(int ExitCode, string Stdout, string Stderr);

    private string T(string chineseSimplified, string english)
    {
        return _i18n.Text(english, chineseSimplified);
    }

    private const int JobObjectExtendedLimitInformationClass = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr securityAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoType, IntPtr jobObjectInfo, uint jobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }
}
















