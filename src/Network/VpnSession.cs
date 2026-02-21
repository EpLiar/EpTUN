using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace EpTUN;

public sealed class VpnSession
{
    private readonly AppConfig _config;
    private readonly string _configDirectory;
    private readonly bool? _bypassCnOverride;
    private readonly TextWriter _log;
    private readonly TextWriter _error;
    private readonly WindowsRouteManager _routeManager;
    private readonly List<ManagedRoute> _managedRoutes = new();

    private Process? _tun2SocksProcess;

    public VpnSession(
        AppConfig config,
        string configPath,
        TextWriter log,
        TextWriter error,
        bool? bypassCnOverride = null)
    {
        _config = config;
        _configDirectory = Path.GetDirectoryName(Path.GetFullPath(configPath))
            ?? AppContext.BaseDirectory;
        _bypassCnOverride = bypassCnOverride;
        _log = log;
        _error = error;
        _routeManager = new WindowsRouteManager(log, error);
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var defaultRoute = await ResolveDefaultRouteAsync(cancellationToken);
        var defaultRouteV6 = await ResolveDefaultRouteV6Async(cancellationToken);
        var proxyUri = await ResolveProxyUriAsync(cancellationToken);
        await EnsureProxyEndpointReachableAsync(proxyUri, cancellationToken);
        var proxyHosts = await ResolveProxyHostsAsync(proxyUri, cancellationToken);
        var dynamicExcludeRoutes = await ResolveDynamicExcludeRoutesAsync(cancellationToken);
        var cnExcludeRoutes = ResolveCnExcludeRoutes();

        _log.WriteLine($"[INFO] Proxy endpoint: {proxyUri}");
        _log.WriteLine($"[INFO] Default gateway before VPN: {defaultRoute.Gateway}");
        if (defaultRouteV6 is not null)
        {
            _log.WriteLine($"[INFO] IPv6 default gateway before VPN: {defaultRouteV6.Gateway} (IF {defaultRouteV6.InterfaceIndex})");
        }
        else
        {
            _log.WriteLine("[INFO] IPv6 default route not found. IPv6 bypass routes will be skipped.");
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
                    $"tun2socks exited early with code {_tun2SocksProcess.ExitCode}. " +
                    "Check previous [tun2socks] logs for details.");
            }

            await EnsureTunInterfaceConfiguredAsync(cancellationToken);
            var tunInterfaceIndex = await _routeManager.GetInterfaceIndexByNameAsync(_config.Vpn.InterfaceName, cancellationToken);
            _log.WriteLine($"[INFO] TUN interface index: {tunInterfaceIndex}");
            await ApplyRoutesAsync(
                defaultRoute.Gateway,
                defaultRouteV6,
                proxyHosts,
                tunInterfaceIndex,
                dynamicExcludeRoutes,
                cnExcludeRoutes,
                cancellationToken);

            _log.WriteLine("[INFO] VPN routes applied. Press Ctrl+C to stop.");

            var exitTask = _tun2SocksProcess.WaitForExitAsync(CancellationToken.None);
            var cancelTask = Task.Delay(Timeout.Infinite, cancellationToken);

            var completed = await Task.WhenAny(exitTask, cancelTask);
            if (completed == exitTask && !cancellationToken.IsCancellationRequested)
            {
                throw new InvalidOperationException($"tun2socks exited unexpectedly with code {_tun2SocksProcess.ExitCode}.");
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
            _error.WriteLine($"[WARN] Failed to resolve IPv6 default route: {ex.Message}");
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
            return await V2RayATouchClient.ResolveProxyUriAsync(_config.V2RayA, _config.Proxy, _log, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _error.WriteLine($"[WARN] v2rayA /api/ports failed, fallback to proxy config: {ex.Message}");
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

    private static async Task EnsureProxyEndpointReachableAsync(Uri proxyUri, CancellationToken cancellationToken)
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
                $"Proxy endpoint is not reachable: {proxyUri}. Check local proxy service and port. {ex.Message}");
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
            return await V2RayATouchClient.ResolveExcludeCidrsAsync(_config.V2RayA, _log, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _error.WriteLine($"[WARN] v2rayA auto excludeCidrs failed: {ex.Message}");
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
            _error.WriteLine($"[WARN] CN dat file not found: {cnDatPath}");
            return [];
        }

        try
        {
            var routes = GeoIpCnDatProvider.LoadCnCidrs(cnDatPath);
            var ipv4Count = routes.Count(static x => x.IsIPv4);
            var ipv6Count = routes.Count - ipv4Count;
            _log.WriteLine(
                $"[INFO] Bypass CN: loaded {ipv4Count} IPv4 + {ipv6Count} IPv6 CIDRs from {cnDatPath}");
            return routes;
        }
        catch (Exception ex)
        {
            _error.WriteLine($"[WARN] Failed to load CN routes: {ex.Message}");
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
                    $"[INFO] TUN interface '{_config.Vpn.InterfaceName}' configured: {_config.Vpn.TunAddress}/{_config.Vpn.TunMask}");
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
            $"Failed to configure TUN interface '{_config.Vpn.InterfaceName}'. " +
            "Check vpn.interfaceName and tun2socks device name in appsettings.json.",
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
                $"Failed to set TUN address. netsh {args}\n{result.Stdout}\n{result.Stderr}".Trim());
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
                $"Failed to set primary DNS. netsh {setPrimaryArgs}\n{primaryResult.Stdout}\n{primaryResult.Stderr}".Trim());
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
                    $"Failed to add DNS server {dns}. netsh {addArgs}\n{addResult.Stdout}\n{addResult.Stderr}".Trim());
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
                    $"[INFO] IPv6 include routes are not configured; skipped {skippedTotalV6} IPv6 exclude routes ({skippedBaseV6} static, {skippedDynamicV6} dynamic, {skippedCnV6} CN).");
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
                    $"[INFO] Skipped {skippedProxyIpv6NoInclude} IPv6 proxy bypass routes because IPv6 include routes are not configured.");
            }

            if (skippedProxyIpv6NoDefaultRoute > 0)
            {
                _error.WriteLine(
                    $"[WARN] Skipped {skippedProxyIpv6NoDefaultRoute} IPv6 proxy bypass routes: no IPv6 default route.");
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
                $"[INFO] Added {effectiveDynamicExcludeRoutes.Count} dynamic exclude routes from v2rayA ({dynamicV4} IPv4, {dynamicV6} IPv6).");
        }

        if (effectiveCnExcludeRoutes.Count > 0)
        {
            var cnV4 = effectiveCnExcludeRoutes.Count(static x => x.IsIPv4);
            var cnV6 = effectiveCnExcludeRoutes.Count - cnV4;
            _log.WriteLine(
                $"[INFO] Added {effectiveCnExcludeRoutes.Count} CN exclude routes ({cnV4} IPv4, {cnV6} IPv6).");
        }

        var verboseExcludeLogs = allExcludeRoutes.Length <= 200;
        if (!verboseExcludeLogs)
        {
            _log.WriteLine($"[INFO] Applying {allExcludeRoutes.Length} exclude routes (route-level logs suppressed).");
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
                _log.WriteLine($"[INFO] Applied {i + 1}/{allExcludeRoutes.Length} exclude routes...");
            }
        }

        excludeRouteApplyStopwatch.Stop();
        if (allExcludeRoutes.Length > 0)
        {
            _log.WriteLine(
                $"[INFO] Exclude routes apply completed in {excludeRouteApplyStopwatch.Elapsed.TotalSeconds:F1}s for {allExcludeRoutes.Length} routes.");
        }

        if (skippedIpv6Exclude > 0)
        {
            _error.WriteLine($"[WARN] Skipped {skippedIpv6Exclude} IPv6 exclude routes: no IPv6 default route.");
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

        _log.WriteLine($"[INFO] Core include routes applied ({includeRoutes.Length}).");
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
            _log.WriteLine($"[INFO] Route {route.Network}/{route.PrefixLength} -> {nextHop} (metric {metric})");
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
            throw new FileNotFoundException($"tun2socks executable not found: {exePath}");
        }

        EnsureWintunDllAvailable(exePath);

        var arguments = BuildTun2SocksArgs(proxyUri);
        _log.WriteLine($"[INFO] Starting tun2socks: {exePath} {arguments}");

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
            throw new InvalidOperationException("Failed to start tun2socks.");
        }

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
            _log.WriteLine($"[INFO] Copied wintun.dll to: {targetPath}");
            return;
        }

        var configuredPath = _config.Tun2Socks.WintunDllPath.Trim();
        throw new FileNotFoundException(
            $"wintun.dll not found. Checked tun2Socks.wintunDllPath='{configuredPath}' and fallback paths. " +
            "Put x64 wintun.dll in the same directory as tun2socks.exe or update tun2Socks.wintunDllPath.",
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
        if (_tun2SocksProcess is null)
        {
            return;
        }

        try
        {
            if (!_tun2SocksProcess.HasExited)
            {
                _tun2SocksProcess.Kill(entireProcessTree: true);
                _tun2SocksProcess.WaitForExit(2000);
            }
        }
        catch (Exception ex)
        {
            _error.WriteLine($"[WARN] Failed to stop tun2socks: {ex.Message}");
        }

        _tun2SocksProcess.Dispose();
        _tun2SocksProcess = null;
    }

    private static string QuoteArg(string value)
    {
        return $"\"{value.Replace("\"", string.Empty)}\"";
    }

    private static async Task<CommandResult> RunCommandAsync(
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
            throw new InvalidOperationException($"Failed to start process: {fileName}");
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
}
















