using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EpTUN;

internal static class StartupRecovery
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(2);
    private static readonly Regex RouteLineRegex = new(
        @"^\s*(?<dest>\S+)\s+(?<mask>\S+)\s+(?<gateway>\S+)\s+(?<iface>\S+)\s+(?<metric>\d+)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex RouteLineV6Regex = new(
        @"^\s*(?<if>\d+)\s+(?<metric>\d+)\s+(?<dest>\S+)\s+(?<gateway>\S+)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static async Task<string[]> RecoverAsync(string configPath, CancellationToken cancellationToken = default)
    {
        var i18n = new Localizer(UiLanguageResolver.ResolveFromConfigPath(configPath));
        var messages = new List<string>();
        var log = new BufferedTextWriter(messages);
        var totalStopwatch = Stopwatch.StartNew();
        long processScanElapsedMs = 0;
        long routeScanElapsedMs = 0;
        long routeResetElapsedMs = 0;

        AppConfig? config;
        try
        {
            config = await LoadConfigAsync(configPath, cancellationToken);
        }
        catch (Exception ex)
        {
            log.WriteLine(T(
                i18n,
                $"[WARN] Startup recovery skipped because the config could not be loaded: {ex.Message}",
                $"[WARN] 启动恢复已跳过：无法加载配置。{ex.Message}"));
            return messages.ToArray();
        }

        var phaseStopwatch = Stopwatch.StartNew();
        var staleProcesses = FindRelatedTun2SocksProcesses(config, configPath, i18n, log);
        processScanElapsedMs = phaseStopwatch.ElapsedMilliseconds;

        phaseStopwatch.Restart();
        var hasResidualRoutes = false;
        try
        {
            hasResidualRoutes = await HasResidualRoutesAsync(config, cancellationToken);
        }
        catch (Exception ex)
        {
            log.WriteLine(T(
                i18n,
                $"[WARN] Startup recovery skipped route-table inspection: {ex.Message}",
                $"[WARN] 启动恢复已跳过路由表检查：{ex.Message}"));
        }

        routeScanElapsedMs = phaseStopwatch.ElapsedMilliseconds;
        if (staleProcesses.Count == 0 && !hasResidualRoutes)
        {
            totalStopwatch.Stop();
            WriteTimingLogIfSlow(i18n, log, processScanElapsedMs, routeScanElapsedMs, routeResetElapsedMs, totalStopwatch.ElapsedMilliseconds);
            return messages.ToArray();
        }

        var killedCount = 0;
        foreach (var process in staleProcesses)
        {
            if (await TryKillProcessAsync(process, i18n, log, cancellationToken))
            {
                killedCount++;
            }
        }

        phaseStopwatch.Restart();
        var resetSummary = await TryResetRoutesAsync(config, configPath, i18n, log, cancellationToken);
        routeResetElapsedMs = phaseStopwatch.ElapsedMilliseconds;
        if (killedCount > 0 || resetSummary.AttemptedRouteDeletes > 0 || resetSummary.StaticStateDetected)
        {
            log.WriteLine(T(
                i18n,
                $"[INFO] Startup recovery completed: killed {killedCount} stale tun2socks process(es), attempted to reset {resetSummary.AttemptedRouteDeletes} EpTUN-managed route(s).",
                $"[INFO] 启动恢复完成：已结束 {killedCount} 个遗留 tun2socks 进程，并尝试重置 {resetSummary.AttemptedRouteDeletes} 条 EpTUN 管理的路由。"));
        }

        totalStopwatch.Stop();
        WriteTimingLogIfSlow(i18n, log, processScanElapsedMs, routeScanElapsedMs, routeResetElapsedMs, totalStopwatch.ElapsedMilliseconds);
        return messages.ToArray();
    }

    private static async Task<AppConfig> LoadConfigAsync(string configPath, CancellationToken cancellationToken)
    {
        var json = await File.ReadAllTextAsync(configPath, cancellationToken);
        var config = JsonSerializer.Deserialize<AppConfig>(json, AppConfig.SerializerOptions)
            ?? throw new InvalidOperationException("Failed to parse configuration.");
        config.Validate();
        return config;
    }

    private static IReadOnlyList<ProcessSnapshot> FindRelatedTun2SocksProcesses(
        AppConfig config,
        string configPath,
        Localizer i18n,
        TextWriter log)
    {
        var expectedExePath = ResolveTun2SocksExecutablePath(config, configPath);
        var matches = new List<ProcessSnapshot>();
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(expectedExePath)))
        {
            try
            {
                using (process)
                {
                    if (process.Id == Environment.ProcessId)
                    {
                        continue;
                    }

                    var processPath = process.MainModule?.FileName;
                    if (!string.Equals(NormalizePath(processPath), NormalizePath(expectedExePath), StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    matches.Add(new ProcessSnapshot(process.Id, processPath));
                }
            }
            catch
            {
                // Ignore processes that can't be inspected.
            }
        }

        foreach (var match in matches)
        {
            log.WriteLine(T(
                i18n,
                $"[WARN] Detected stale tun2socks process from a previous run: PID={match.ProcessId}, Path={match.ExecutablePath ?? "unknown"}",
                $"[WARN] 检测到上次运行遗留的 tun2socks 进程：PID={match.ProcessId}，路径={match.ExecutablePath ?? "未知"}"));
        }

        return matches;
    }

    private static async Task<bool> TryKillProcessAsync(
        ProcessSnapshot snapshot,
        Localizer i18n,
        TextWriter log,
        CancellationToken cancellationToken)
    {
        try
        {
            using var process = Process.GetProcessById(snapshot.ProcessId);
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(cancellationToken);
            log.WriteLine(T(
                i18n,
                $"[INFO] Terminated stale tun2socks process PID={snapshot.ProcessId}.",
                $"[INFO] 已终止遗留 tun2socks 进程 PID={snapshot.ProcessId}。"));
            return true;
        }
        catch (Exception ex)
        {
            log.WriteLine(T(
                i18n,
                $"[WARN] Failed to terminate stale tun2socks process PID={snapshot.ProcessId}: {ex.Message}",
                $"[WARN] 终止遗留 tun2socks 进程 PID={snapshot.ProcessId} 失败：{ex.Message}"));
            return false;
        }
    }

    private static async Task<bool> HasResidualRoutesAsync(AppConfig config, CancellationToken cancellationToken)
    {
        var includeRoutes = config.Vpn.IncludeCidrs
            .Select(CidrRoute.Parse)
            .Distinct()
            .ToArray();
        var includeRoutesV4 = includeRoutes
            .Where(static route => route.IsIPv4)
            .ToArray();
        var includeRoutesV6 = includeRoutes
            .Where(static route => route.IsIPv6)
            .Select(static route => $"{route.Network}/{route.PrefixLength}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (includeRoutesV4.Length > 0)
        {
            var result4 = await RunCommandAsync("route", ["print", "-4"], cancellationToken);
            if (result4.ExitCode == 0)
            {
                var tunGateway = config.Vpn.TunGateway;
                using var reader = new StringReader(result4.Stdout);
                while (reader.ReadLine() is { } line)
                {
                    var match = RouteLineRegex.Match(line);
                    if (!match.Success)
                    {
                        continue;
                    }

                    if (!match.Groups["gateway"].Value.Equals(tunGateway, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    foreach (var route in includeRoutesV4)
                    {
                        if (match.Groups["dest"].Value.Equals(route.Network, StringComparison.OrdinalIgnoreCase) &&
                            match.Groups["mask"].Value.Equals(route.Mask, StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }
                }
            }
        }

        if (includeRoutesV6.Count == 0)
        {
            return false;
        }

        var tunInterfaceIndex = await TryGetInterfaceIndexAsync(config.Vpn.InterfaceName, cancellationToken);
        if (!tunInterfaceIndex.HasValue)
        {
            return false;
        }

        var result6 = await RunCommandAsync("route", ["print", "-6"], cancellationToken);
        if (result6.ExitCode != 0)
        {
            return false;
        }

        using var reader6 = new StringReader(result6.Stdout);
        while (reader6.ReadLine() is { } line)
        {
            var match = RouteLineV6Regex.Match(line);
            if (!match.Success)
            {
                continue;
            }

            if (!int.TryParse(match.Groups["if"].Value, out var interfaceIndex) || interfaceIndex != tunInterfaceIndex.Value)
            {
                continue;
            }

            if (includeRoutesV6.Contains(match.Groups["dest"].Value))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<RouteResetSummary> TryResetRoutesAsync(
        AppConfig config,
        string configPath,
        Localizer i18n,
        TextWriter log,
        CancellationToken cancellationToken)
    {
        var routeManager = new WindowsRouteManager(log, log, i18n);
        var attemptedDeletes = 0;

        var defaultGateway = config.Vpn.DefaultGatewayOverride;
        DefaultRoute? defaultRoute = null;
        try
        {
            defaultRoute = string.IsNullOrWhiteSpace(defaultGateway)
                ? await routeManager.GetDefaultRouteAsync(cancellationToken)
                : new DefaultRoute(IPAddress.Parse(defaultGateway), IPAddress.Any, 0);
        }
        catch (Exception ex)
        {
            log.WriteLine(T(
                i18n,
                $"[WARN] Startup route reset could not determine the IPv4 default gateway: {ex.Message}",
                $"[WARN] 启动路由重置时无法确定 IPv4 默认网关：{ex.Message}"));
        }

        DefaultRouteV6? defaultRouteV6 = null;
        try
        {
            defaultRouteV6 = await routeManager.GetDefaultRouteV6Async(cancellationToken);
        }
        catch (Exception ex)
        {
            log.WriteLine(T(
                i18n,
                $"[WARN] Startup route reset could not determine the IPv6 default gateway: {ex.Message}",
                $"[WARN] 启动路由重置时无法确定 IPv6 默认网关：{ex.Message}"));
        }

        var tunInterfaceIndex = await TryGetInterfaceIndexAsync(config.Vpn.InterfaceName, cancellationToken);
        var includeRoutes = config.Vpn.IncludeCidrs.Select(CidrRoute.Parse).Distinct().ToArray();
        var staticExcludeRoutes = config.Vpn.ExcludeCidrs.Select(CidrRoute.Parse).Distinct().ToArray();
        var dynamicExcludeRoutes = await ResolveDynamicExcludeRoutesAsync(config, log, i18n, cancellationToken);
        var cnExcludeRoutes = ResolveCnExcludeRoutes(config, configPath, log, i18n);
        var proxyRoutes = await ResolveProxyBypassRoutesAsync(config, cancellationToken);

        var staticStateDetected = includeRoutes.Length > 0;
        foreach (var route in includeRoutes)
        {
            if (route.IsIPv4)
            {
                attemptedDeletes++;
                await routeManager.DeleteRouteAsync(route, IPAddress.Parse(config.Vpn.TunGateway), cancellationToken, suppressWarning: true);
            }
            else if (tunInterfaceIndex.HasValue)
            {
                attemptedDeletes++;
                await routeManager.DeleteRouteAsync(route, gateway: null, cancellationToken, suppressWarning: true, interfaceIndex: tunInterfaceIndex.Value);
            }
        }

        var bypassRoutes = staticExcludeRoutes
            .Concat(dynamicExcludeRoutes)
            .Concat(cnExcludeRoutes)
            .Concat(proxyRoutes)
            .Distinct()
            .ToArray();

        foreach (var route in bypassRoutes)
        {
            if (route.IsIPv4)
            {
                if (defaultRoute is null)
                {
                    continue;
                }

                attemptedDeletes++;
                await routeManager.DeleteRouteAsync(route, defaultRoute.Gateway, cancellationToken, suppressWarning: true);
            }
            else
            {
                if (defaultRouteV6 is null)
                {
                    continue;
                }

                attemptedDeletes++;
                await routeManager.DeleteRouteAsync(
                    route,
                    defaultRouteV6.Gateway,
                    cancellationToken,
                    suppressWarning: true,
                    interfaceIndex: defaultRouteV6.InterfaceIndex);
            }
        }

        return new RouteResetSummary(attemptedDeletes, staticStateDetected);
    }

    private static async Task<int?> TryGetInterfaceIndexAsync(string interfaceName, CancellationToken cancellationToken)
    {
        try
        {
            var routeManager = new WindowsRouteManager(TextWriter.Null, TextWriter.Null, new Localizer(UiLanguage.English));
            return await routeManager.GetInterfaceIndexByNameAsync(interfaceName, cancellationToken);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<IReadOnlyCollection<CidrRoute>> ResolveDynamicExcludeRoutesAsync(
        AppConfig config,
        TextWriter log,
        Localizer i18n,
        CancellationToken cancellationToken)
    {
        if (!config.V2RayA.Enabled)
        {
            return [];
        }

        try
        {
            return await V2RayATouchClient.ResolveExcludeCidrsAsync(config.V2RayA, log, i18n, cancellationToken);
        }
        catch
        {
            return [];
        }
    }

    private static IReadOnlyCollection<CidrRoute> ResolveCnExcludeRoutes(
        AppConfig config,
        string configPath,
        TextWriter log,
        Localizer i18n)
    {
        if (!config.Vpn.BypassCn)
        {
            return [];
        }

        var cnDatPath = ResolveCnDatPath(config, configPath);
        if (!File.Exists(cnDatPath))
        {
            log.WriteLine(T(i18n, $"[WARN] Startup route reset could not find CN dat file: {cnDatPath}", $"[WARN] 启动路由重置时未找到 CN dat 文件：{cnDatPath}"));
            return [];
        }

        try
        {
            return GeoIpCnDatProvider.LoadCnCidrs(cnDatPath);
        }
        catch (Exception ex)
        {
            log.WriteLine(T(i18n, $"[WARN] Failed to load CN routes for startup reset: {ex.Message}", $"[WARN] 启动重置时加载 CN 路由失败：{ex.Message}"));
            return [];
        }
    }

    private static async Task<IReadOnlyCollection<CidrRoute>> ResolveProxyBypassRoutesAsync(AppConfig config, CancellationToken cancellationToken)
    {
        if (!config.Vpn.AddBypassRouteForProxyHost)
        {
            return [];
        }

        var host = ResolveProxyHost(config);
        if (IPAddress.TryParse(host, out var ip))
        {
            return ip.AddressFamily == AddressFamily.InterNetwork
                ? [CidrRoute.Parse($"{ip}/32")]
                : [CidrRoute.Parse($"{ip}/128")];
        }

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);
            return addresses
                .Where(static address => !IPAddress.IsLoopback(address))
                .Where(static address => address.AddressFamily == AddressFamily.InterNetwork || address.AddressFamily == AddressFamily.InterNetworkV6)
                .Select(static address => address.AddressFamily == AddressFamily.InterNetwork
                    ? CidrRoute.Parse($"{address}/32")
                    : CidrRoute.Parse($"{address}/128"))
                .Distinct()
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    private static string ResolveProxyHost(AppConfig config)
    {
        if (config.V2RayA.Enabled && !string.IsNullOrWhiteSpace(config.V2RayA.ProxyHostOverride))
        {
            return config.V2RayA.ProxyHostOverride.Trim();
        }

        return config.Proxy.Host.Trim();
    }

    private static string ResolveTun2SocksExecutablePath(AppConfig config, string configPath)
    {
        var configDirectory = Path.GetDirectoryName(Path.GetFullPath(configPath))
            ?? AppContext.BaseDirectory;
        var configured = config.Tun2Socks.ExecutablePath.Trim();
        if (Path.IsPathRooted(configured))
        {
            return Path.GetFullPath(configured);
        }

        var candidates = new[]
        {
            Path.GetFullPath(Path.Combine(configDirectory, configured)),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, configured)),
            Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, configured)),
            Path.GetFullPath(Path.Combine(configDirectory, Path.GetFileName(configured))),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, Path.GetFileName(configured)))
        };

        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }

    private static string ResolveCnDatPath(AppConfig config, string configPath)
    {
        var configDirectory = Path.GetDirectoryName(Path.GetFullPath(configPath))
            ?? AppContext.BaseDirectory;
        var configured = config.Vpn.CnDatPath.Trim();
        if (Path.IsPathRooted(configured))
        {
            return Path.GetFullPath(configured);
        }

        var candidates = new[]
        {
            Path.GetFullPath(Path.Combine(configDirectory, configured)),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, configured)),
            Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, configured)),
            Path.GetFullPath(Path.Combine(configDirectory, Path.GetFileName(configured))),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, Path.GetFileName(configured)))
        };

        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }

    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFullPath(path.Trim());
        }
        catch
        {
            return path.Trim();
        }
    }

    private static async Task<ProcessResult> RunCommandAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(CommandTimeout);
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start process: {fileName}");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Ignore failures while timing out an external diagnostic command.
            }

            throw new TimeoutException($"Command timed out: {fileName} {string.Join(" ", arguments)}");
        }

        return new ProcessResult(process.ExitCode, await stdoutTask, await stderrTask);
    }

    private static void WriteTimingLogIfSlow(
        Localizer i18n,
        TextWriter log,
        long processScanElapsedMs,
        long routeScanElapsedMs,
        long routeResetElapsedMs,
        long totalElapsedMs)
    {
        if (totalElapsedMs < 1000)
        {
            return;
        }

        log.WriteLine(T(
            i18n,
            $"[INFO] Startup recovery timing: process scan={processScanElapsedMs} ms, route scan={routeScanElapsedMs} ms, route reset={routeResetElapsedMs} ms, total={totalElapsedMs} ms.",
            $"[INFO] 启动恢复耗时：进程扫描={processScanElapsedMs} ms，路由扫描={routeScanElapsedMs} ms，路由重置={routeResetElapsedMs} ms，总计={totalElapsedMs} ms。"));
    }

    private static string T(Localizer i18n, string english, string chineseSimplified)
    {
        return i18n.Text(english, chineseSimplified);
    }

    private sealed record ProcessSnapshot(int ProcessId, string? ExecutablePath);

    private sealed record RouteResetSummary(int AttemptedRouteDeletes, bool StaticStateDetected);

    private sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);

    private sealed class BufferedTextWriter(List<string> messages) : TextWriter
    {
        private readonly List<string> _messages = messages;

        public override Encoding Encoding => Encoding.UTF8;

        public override void WriteLine(string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                _messages.Add(value);
            }
        }
    }
}
