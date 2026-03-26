using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace EpTUN;

public sealed record DefaultRoute(IPAddress Gateway, IPAddress InterfaceAddress, int Metric);
public sealed record DefaultRouteV6(IPAddress Gateway, int InterfaceIndex, int Metric);

internal sealed class WindowsRouteManager(TextWriter log, TextWriter error, Localizer i18n)
{
    private static readonly IPAddress NativeDefaultIpv4ProbeAddress = IPAddress.Parse("8.8.8.8");
    private static readonly Regex RouteLineRegex = new(
        @"^\s*(?<dest>\S+)\s+(?<mask>\S+)\s+(?<gateway>\S+)\s+(?<iface>\S+)\s+(?<metric>\d+)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex RouteLineV6Regex = new(
        @"^\s*(?<if>\d+)\s+(?<metric>\d+)\s+(?<dest>\S+)\s+(?<gateway>\S+)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex InterfaceLineRegex = new(
        @"^\s*(?<idx>\d+)\s+\d+\s+\d+\s+\S+\s+(?<name>.+?)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly TextWriter _log = log;
    private readonly TextWriter _error = error;
    private readonly Localizer _i18n = i18n;
    private readonly Dictionary<string, int> _ipv4BestInterfaceByGateway = new(StringComparer.Ordinal);
    private readonly object _ipv4InterfaceCacheLock = new();
    private bool _nativeIpv4RouteApiEnabled = OperatingSystem.IsWindows();

    public async Task<DefaultRoute> GetDefaultRouteAsync(CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows() && TryGetDefaultRouteNative(out var nativeRoute))
        {
            return nativeRoute;
        }

        var result = await RunCommandAsync("route", "print -4", cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(T($"Failed to read route table: {result.Stderr}".Trim(), $"读取路由表失败：{result.Stderr}".Trim()));
        }

        var candidates = new List<DefaultRoute>();
        using var reader = new StringReader(result.Stdout);
        while (reader.ReadLine() is { } line)
        {
            var match = RouteLineRegex.Match(line);
            if (!match.Success)
            {
                continue;
            }

            if (!match.Groups["dest"].Value.Equals("0.0.0.0", StringComparison.Ordinal) ||
                !match.Groups["mask"].Value.Equals("0.0.0.0", StringComparison.Ordinal))
            {
                continue;
            }

            if (!IPAddress.TryParse(match.Groups["gateway"].Value, out var gateway) ||
                gateway.AddressFamily != AddressFamily.InterNetwork)
            {
                continue;
            }

            if (!IPAddress.TryParse(match.Groups["iface"].Value, out var iface) ||
                iface.AddressFamily != AddressFamily.InterNetwork)
            {
                continue;
            }

            if (!int.TryParse(match.Groups["metric"].Value, out var metric))
            {
                continue;
            }

            candidates.Add(new DefaultRoute(gateway, iface, metric));
        }

        var best = candidates.OrderBy(static x => x.Metric).FirstOrDefault();
        if (best is null)
        {
            throw new InvalidOperationException(T("No IPv4 default route was found.", "未找到 IPv4 默认路由。"));
        }

        return best;
    }

    private bool TryGetDefaultRouteNative(out DefaultRoute route)
    {
        route = null!;

        var code = NativeMethods.GetBestRoute(ToIpv4UInt32(NativeDefaultIpv4ProbeAddress), 0, out var row);
        if (code != NativeMethods.NO_ERROR || row.ForwardIfIndex == 0)
        {
            return false;
        }

        var gateway = FromIpv4UInt32(row.ForwardNextHop);
        if (gateway.AddressFamily != AddressFamily.InterNetwork || gateway.Equals(IPAddress.Any))
        {
            return false;
        }

        var interfaceAddress = TryGetInterfaceIpv4Address(checked((int)row.ForwardIfIndex)) ?? IPAddress.Any;
        route = new DefaultRoute(gateway, interfaceAddress, checked((int)row.ForwardMetric1));
        return true;
    }

    private static IPAddress? TryGetInterfaceIpv4Address(int interfaceIndex)
    {
        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            IPInterfaceProperties properties;
            try
            {
                properties = networkInterface.GetIPProperties();
            }
            catch
            {
                continue;
            }

            IPv4InterfaceProperties? ipv4Properties;
            try
            {
                ipv4Properties = properties.GetIPv4Properties();
            }
            catch
            {
                continue;
            }

            if (ipv4Properties is null || ipv4Properties.Index != interfaceIndex)
            {
                continue;
            }

            foreach (var address in properties.UnicastAddresses)
            {
                if (address.Address.AddressFamily == AddressFamily.InterNetwork)
                {
                    return address.Address;
                }
            }
        }

        return null;
    }

    public async Task<DefaultRouteV6?> GetDefaultRouteV6Async(CancellationToken cancellationToken)
    {
        var result = await RunCommandAsync("route", "print -6", cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(T($"Failed to read IPv6 route table: {result.Stderr}".Trim(), $"读取 IPv6 路由表失败：{result.Stderr}".Trim()));
        }

        var candidates = new List<DefaultRouteV6>();
        using var reader = new StringReader(result.Stdout);
        while (reader.ReadLine() is { } line)
        {
            var match = RouteLineV6Regex.Match(line);
            if (!match.Success)
            {
                continue;
            }

            if (!match.Groups["dest"].Value.Equals("::/0", StringComparison.Ordinal))
            {
                continue;
            }

            var gatewayText = match.Groups["gateway"].Value;
            if (gatewayText.Equals("On-link", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!IPAddress.TryParse(gatewayText, out var gateway) ||
                gateway.AddressFamily != AddressFamily.InterNetworkV6)
            {
                continue;
            }

            if (!int.TryParse(match.Groups["if"].Value, out var interfaceIndex) ||
                !int.TryParse(match.Groups["metric"].Value, out var metric))
            {
                continue;
            }

            candidates.Add(new DefaultRouteV6(gateway, interfaceIndex, metric));
        }

        return candidates.OrderBy(static x => x.Metric).FirstOrDefault();
    }

    public Task AddRouteAsync(
        CidrRoute route,
        IPAddress? gateway,
        int metric,
        CancellationToken cancellationToken,
        int? interfaceIndex = null,
        bool logCommand = true,
        bool replaceIfExists = false)
    {
        if (route.IsIPv4)
        {
            return AddIpv4RouteAsync(route, gateway, metric, cancellationToken, interfaceIndex, logCommand, replaceIfExists);
        }

        return AddIpv6RouteAsync(route, gateway, metric, cancellationToken, interfaceIndex, logCommand, replaceIfExists);
    }

    private Task AddIpv4RouteAsync(
        CidrRoute route,
        IPAddress? gateway,
        int metric,
        CancellationToken cancellationToken,
        int? interfaceIndex,
        bool logCommand,
        bool replaceIfExists)
    {
        if (gateway is null || gateway.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new InvalidOperationException(T($"IPv4 route {route.Network}/{route.PrefixLength} requires an IPv4 gateway.", $"IPv4 路由 {route.Network}/{route.PrefixLength} 需要 IPv4 网关。"));
        }

        if (_nativeIpv4RouteApiEnabled &&
            TryResolveIpv4InterfaceIndex(gateway, interfaceIndex, out var resolvedInterfaceIndex))
        {
            try
            {
                AddIpv4RouteNative(route, gateway, metric, resolvedInterfaceIndex, replaceIfExists, logCommand);
                return Task.CompletedTask;
            }
            catch (InvalidOperationException ex)
            {
                _nativeIpv4RouteApiEnabled = false;
                _error.WriteLine(
                    T(
                        $"[WARN] Native IPv4 route API disabled after failure; falling back to route.exe. {ex.Message}",
                        $"[WARN] 原生 IPv4 路由 API 调用失败，已禁用并回退到 route.exe。{ex.Message}"));
            }
        }

        var args = $"ADD {route.Network} MASK {route.Mask} {gateway} METRIC {metric}";
        if (interfaceIndex.HasValue)
        {
            args += $" IF {interfaceIndex.Value}";
        }

        if (replaceIfExists)
        {
            return AddRouteWithReplaceAsync(
                route,
                gateway,
                "route",
                args,
                cancellationToken,
                interfaceIndex,
                logCommand);
        }

        return RunCheckedAsync("route", args, cancellationToken, logCommand);
    }

    private Task AddIpv6RouteAsync(
        CidrRoute route,
        IPAddress? gateway,
        int metric,
        CancellationToken cancellationToken,
        int? interfaceIndex,
        bool logCommand,
        bool replaceIfExists)
    {
        if (gateway is not null && gateway.AddressFamily != AddressFamily.InterNetworkV6)
        {
            throw new InvalidOperationException(T($"IPv6 route {route.Network}/{route.PrefixLength} requires an IPv6 gateway.", $"IPv6 路由 {route.Network}/{route.PrefixLength} 需要 IPv6 网关。"));
        }

        if (gateway is null && !interfaceIndex.HasValue)
        {
            throw new InvalidOperationException(
                T(
                    $"IPv6 route {route.Network}/{route.PrefixLength} requires gateway or interface index.",
                    $"IPv6 路由 {route.Network}/{route.PrefixLength} 需要网关或接口索引。"));
        }

        var args = $"interface ipv6 add route prefix={route.Network}/{route.PrefixLength}";
        if (interfaceIndex.HasValue)
        {
            args += $" interface={interfaceIndex.Value}";
        }

        if (gateway is not null)
        {
            args += $" nexthop={gateway}";
        }

        args += $" metric={metric} store=active";

        if (replaceIfExists)
        {
            return AddRouteWithReplaceAsync(
                route,
                gateway,
                "netsh",
                args,
                cancellationToken,
                interfaceIndex,
                logCommand);
        }

        return RunCheckedAsync("netsh", args, cancellationToken, logCommand);
    }

    private async Task AddRouteWithReplaceAsync(
        CidrRoute route,
        IPAddress? gateway,
        string fileName,
        string arguments,
        CancellationToken cancellationToken,
        int? interfaceIndex,
        bool logCommand)
    {
        var result = await RunCommandAsync(fileName, arguments, cancellationToken);
        if (result.ExitCode == 0)
        {
            if (logCommand)
            {
                _log.WriteLine(T($"[INFO] Command executed: {fileName} {arguments}", $"[INFO] 已执行命令：{fileName} {arguments}"));
            }

            return;
        }

        if (!IsAlreadyExistsError(result))
        {
            throw new InvalidOperationException(
                T(
                    $"Command failed: {fileName} {arguments}\n{result.Stderr}".Trim(),
                    $"命令执行失败：{fileName} {arguments}\n{result.Stderr}".Trim()));
        }

        await DeleteRouteAsync(route, gateway, cancellationToken, suppressWarning: true, interfaceIndex);
        await RunCheckedAsync(fileName, arguments, cancellationToken, logCommand);
    }

    private static bool IsAlreadyExistsError(ProcessResult result)
    {
        var text = $"{result.Stdout}\n{result.Stderr}";
        return text.Contains("already exists", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("已存在", StringComparison.Ordinal);
    }

    public async Task<int> GetInterfaceIndexByNameAsync(string interfaceName, CancellationToken cancellationToken)
    {
        var result = await RunCommandAsync("netsh", "interface ipv4 show interfaces", cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(T($"Failed to list interfaces: {result.Stderr}".Trim(), $"列出网络接口失败：{result.Stderr}".Trim()));
        }

        using var reader = new StringReader(result.Stdout);
        while (reader.ReadLine() is { } line)
        {
            var match = InterfaceLineRegex.Match(line);
            if (!match.Success)
            {
                continue;
            }

            var name = match.Groups["name"].Value.Trim();
            if (!name.Equals(interfaceName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (int.TryParse(match.Groups["idx"].Value, out var index))
            {
                return index;
            }
        }

        throw new InvalidOperationException(T($"Interface '{interfaceName}' was not found in netsh output.", $"未在 netsh 输出中找到接口“{interfaceName}”。"));
    }

    public async Task DeleteRouteAsync(
        CidrRoute route,
        IPAddress? gateway,
        CancellationToken cancellationToken,
        bool suppressWarning = false,
        int? interfaceIndex = null)
    {
        ProcessResult result;

        if (route.IsIPv4)
        {
            if (gateway is null || gateway.AddressFamily != AddressFamily.InterNetwork)
            {
                if (!suppressWarning)
                {
                    _error.WriteLine(T($"[WARN] Skip deleting IPv4 route {route.Network}/{route.PrefixLength}: missing IPv4 gateway.", $"[WARN] 跳过删除 IPv4 路由 {route.Network}/{route.PrefixLength}：缺少 IPv4 网关。"));
                }

                return;
            }

            if (TryResolveIpv4InterfaceIndex(gateway, interfaceIndex, out var resolvedInterfaceIndex))
            {
                DeleteIpv4RouteNative(route, gateway, resolvedInterfaceIndex, suppressWarning);
                return;
            }

            var args = $"DELETE {route.Network} MASK {route.Mask} {gateway}";
            result = await RunCommandAsync("route", args, cancellationToken);
        }
        else
        {
            if (gateway is not null && gateway.AddressFamily != AddressFamily.InterNetworkV6)
            {
                if (!suppressWarning)
                {
                    _error.WriteLine(T($"[WARN] Skip deleting IPv6 route {route.Network}/{route.PrefixLength}: invalid IPv6 gateway.", $"[WARN] 跳过删除 IPv6 路由 {route.Network}/{route.PrefixLength}：IPv6 网关无效。"));
                }

                return;
            }

            var args = $"interface ipv6 delete route prefix={route.Network}/{route.PrefixLength}";
            if (interfaceIndex.HasValue)
            {
                args += $" interface={interfaceIndex.Value}";
            }

            if (gateway is not null)
            {
                args += $" nexthop={gateway}";
            }

            result = await RunCommandAsync("netsh", args, cancellationToken);
        }

        if (!suppressWarning && result.ExitCode != 0)
        {
            _error.WriteLine(
                T(
                    $"[WARN] Failed to delete route {route.Network}/{route.PrefixLength} via {gateway?.ToString() ?? "on-link"}: {result.Stderr}".Trim(),
                    $"[WARN] 删除路由 {route.Network}/{route.PrefixLength} 经由 {gateway?.ToString() ?? "on-link"} 失败：{result.Stderr}".Trim()));
        }
    }

    private bool TryResolveIpv4InterfaceIndex(IPAddress gateway, int? interfaceIndexOverride, out int interfaceIndex)
    {
        interfaceIndex = 0;
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        if (interfaceIndexOverride.HasValue)
        {
            interfaceIndex = interfaceIndexOverride.Value;
            return interfaceIndex > 0;
        }

        var key = gateway.ToString();
        lock (_ipv4InterfaceCacheLock)
        {
            if (_ipv4BestInterfaceByGateway.TryGetValue(key, out var cached))
            {
                interfaceIndex = cached;
                return true;
            }
        }

        var code = NativeMethods.GetBestInterface(ToIpv4UInt32(gateway), out var bestIfIndex);
        if (code != NativeMethods.NO_ERROR || bestIfIndex == 0)
        {
            return false;
        }

        interfaceIndex = checked((int)bestIfIndex);
        lock (_ipv4InterfaceCacheLock)
        {
            _ipv4BestInterfaceByGateway[key] = interfaceIndex;
        }

        return true;
    }

    private void AddIpv4RouteNative(
        CidrRoute route,
        IPAddress gateway,
        int metric,
        int interfaceIndex,
        bool replaceIfExists,
        bool logCommand)
    {
        var row = CreateIpv4RouteRow(route, gateway, metric, interfaceIndex);
        var addCode = NativeMethods.CreateIpForwardEntry(ref row);
        if (addCode == NativeMethods.NO_ERROR)
        {
            if (logCommand)
            {
                _log.WriteLine(
                    T(
                        $"[INFO] iphlpapi add ipv4 route {route.Network}/{route.PrefixLength} via {gateway} if={interfaceIndex} metric={metric}",
                        $"[INFO] 已通过 iphlpapi 添加 IPv4 路由：{route.Network}/{route.PrefixLength} 经由 {gateway}，接口={interfaceIndex}，metric={metric}"));
            }

            return;
        }

        if (replaceIfExists && addCode == NativeMethods.ERROR_OBJECT_ALREADY_EXISTS)
        {
            DeleteIpv4RouteNative(route, gateway, interfaceIndex, suppressWarning: true);
            row = CreateIpv4RouteRow(route, gateway, metric, interfaceIndex);
            addCode = NativeMethods.CreateIpForwardEntry(ref row);
            if (addCode == NativeMethods.NO_ERROR)
            {
                if (logCommand)
                {
                    _log.WriteLine(
                        T(
                            $"[INFO] iphlpapi replace ipv4 route {route.Network}/{route.PrefixLength} via {gateway} if={interfaceIndex} metric={metric}",
                            $"[INFO] 已通过 iphlpapi 替换 IPv4 路由：{route.Network}/{route.PrefixLength} 经由 {gateway}，接口={interfaceIndex}，metric={metric}"));
                }

                return;
            }
        }

        throw new InvalidOperationException(
            T(
                $"Native route add failed ({addCode}): {GetNativeErrorMessage(addCode)}. Route={route.Network}/{route.PrefixLength}, Gateway={gateway}, IfIndex={interfaceIndex}, Metric={metric}",
                $"原生路由添加失败（{addCode}）：{GetNativeErrorMessage(addCode)}。Route={route.Network}/{route.PrefixLength}, Gateway={gateway}, IfIndex={interfaceIndex}, Metric={metric}"));
    }

    private void DeleteIpv4RouteNative(CidrRoute route, IPAddress gateway, int interfaceIndex, bool suppressWarning)
    {
        var row = CreateIpv4RouteRow(route, gateway, 1, interfaceIndex);
        var deleteCode = NativeMethods.DeleteIpForwardEntry(ref row);
        if (deleteCode == NativeMethods.NO_ERROR || deleteCode == NativeMethods.ERROR_NOT_FOUND)
        {
            return;
        }

        if (!suppressWarning)
        {
            _error.WriteLine(
                T(
                    $"[WARN] Native route delete failed ({deleteCode}): {GetNativeErrorMessage(deleteCode)}. Route={route.Network}/{route.PrefixLength}, Gateway={gateway}, IfIndex={interfaceIndex}",
                    $"[WARN] 原生路由删除失败（{deleteCode}）：{GetNativeErrorMessage(deleteCode)}。Route={route.Network}/{route.PrefixLength}, Gateway={gateway}, IfIndex={interfaceIndex}"));
        }
    }

    private NativeMethods.MibIpForwardRow CreateIpv4RouteRow(
        CidrRoute route,
        IPAddress gateway,
        int metric,
        int interfaceIndex)
    {
        if (!IPAddress.TryParse(route.Network, out var destination) || destination.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new InvalidOperationException(T($"IPv4 route destination is invalid: {route.Network}/{route.PrefixLength}", $"IPv4 路由目标地址无效：{route.Network}/{route.PrefixLength}"));
        }

        if (!IPAddress.TryParse(route.Mask, out var mask) || mask.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new InvalidOperationException(T($"IPv4 route mask is invalid: {route.Network}/{route.PrefixLength} mask {route.Mask}", $"IPv4 路由掩码无效：{route.Network}/{route.PrefixLength} mask {route.Mask}"));
        }

        return new NativeMethods.MibIpForwardRow
        {
            ForwardDest = ToIpv4UInt32(destination),
            ForwardMask = ToIpv4UInt32(mask),
            ForwardPolicy = 0,
            ForwardNextHop = ToIpv4UInt32(gateway),
            ForwardIfIndex = checked((uint)interfaceIndex),
            ForwardType = NativeMethods.MIB_IPROUTE_TYPE_INDIRECT,
            ForwardProto = NativeMethods.MIB_IPPROTO_NETMGMT,
            ForwardAge = 0,
            ForwardNextHopAs = 0,
            // CreateIpForwardEntry can reject very low metrics on some systems.
            ForwardMetric1 = checked((uint)Math.Max(256, metric)),
            ForwardMetric2 = uint.MaxValue,
            ForwardMetric3 = uint.MaxValue,
            ForwardMetric4 = uint.MaxValue,
            ForwardMetric5 = uint.MaxValue
        };
    }

    private uint ToIpv4UInt32(IPAddress ipAddress)
    {
        var bytes = ipAddress.GetAddressBytes();
        if (bytes.Length != 4)
        {
            throw new InvalidOperationException(T($"Not an IPv4 address: {ipAddress}", $"不是 IPv4 地址：{ipAddress}"));
        }

        // MIB_IPFORWARDROW expects IPv4 values in the same representation returned by inet_addr.
        // For IPAddress bytes (network order), that maps to little-endian DWORD layout on Windows.
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }

    private static IPAddress FromIpv4UInt32(uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return new IPAddress(bytes);
    }

    private static string GetNativeErrorMessage(uint code)
    {
        return new Win32Exception(unchecked((int)code)).Message;
    }

    private async Task RunCheckedAsync(string fileName, string arguments, CancellationToken cancellationToken, bool logCommand)
    {
        var result = await RunCommandAsync(fileName, arguments, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                T(
                    $"Command failed: {fileName} {arguments}\n{result.Stderr}".Trim(),
                    $"命令执行失败：{fileName} {arguments}\n{result.Stderr}".Trim()));
        }

        if (logCommand)
        {
            _log.WriteLine(T($"[INFO] Command executed: {fileName} {arguments}", $"[INFO] 已执行命令：{fileName} {arguments}"));
        }
    }

    private async Task<ProcessResult> RunCommandAsync(
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
            throw new InvalidOperationException(T($"Failed to start process: {fileName}", $"启动进程失败：{fileName}"));
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);

        return new ProcessResult(
            process.ExitCode,
            await stdoutTask,
            await stderrTask);
    }

    private sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);

    private string T(string english, string chineseSimplified)
    {
        return _i18n.Text(english, chineseSimplified);
    }

    private static class NativeMethods
    {
        public const uint NO_ERROR = 0;
        public const uint ERROR_NOT_FOUND = 1168;
        public const uint ERROR_OBJECT_ALREADY_EXISTS = 5010;
        public const uint MIB_IPROUTE_TYPE_INDIRECT = 4;
        public const uint MIB_IPPROTO_NETMGMT = 3;

        [DllImport("iphlpapi.dll")]
        public static extern uint GetBestInterface(uint destAddr, out uint bestIfIndex);

        [DllImport("iphlpapi.dll")]
        public static extern uint GetBestRoute(uint destAddr, uint sourceAddr, out MibIpForwardRow bestRoute);

        [DllImport("iphlpapi.dll")]
        public static extern uint CreateIpForwardEntry(ref MibIpForwardRow route);

        [DllImport("iphlpapi.dll")]
        public static extern uint DeleteIpForwardEntry(ref MibIpForwardRow route);

        [StructLayout(LayoutKind.Sequential)]
        public struct MibIpForwardRow
        {
            public uint ForwardDest;
            public uint ForwardMask;
            public uint ForwardPolicy;
            public uint ForwardNextHop;
            public uint ForwardIfIndex;
            public uint ForwardType;
            public uint ForwardProto;
            public uint ForwardAge;
            public uint ForwardNextHopAs;
            public uint ForwardMetric1;
            public uint ForwardMetric2;
            public uint ForwardMetric3;
            public uint ForwardMetric4;
            public uint ForwardMetric5;
        }
    }
}

