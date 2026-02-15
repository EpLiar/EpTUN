using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace EpTUN;

public sealed record DefaultRoute(IPAddress Gateway, IPAddress InterfaceAddress, int Metric);
public sealed record DefaultRouteV6(IPAddress Gateway, int InterfaceIndex, int Metric);

public sealed class WindowsRouteManager(TextWriter log, TextWriter error)
{
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

    public async Task<DefaultRoute> GetDefaultRouteAsync(CancellationToken cancellationToken)
    {
        var result = await RunCommandAsync("route", "print -4", cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"Failed to read route table: {result.Stderr}".Trim());
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
            throw new InvalidOperationException("No IPv4 default route was found.");
        }

        return best;
    }

    public async Task<DefaultRouteV6?> GetDefaultRouteV6Async(CancellationToken cancellationToken)
    {
        var result = await RunCommandAsync("route", "print -6", cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"Failed to read IPv6 route table: {result.Stderr}".Trim());
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
        bool logCommand = true)
    {
        if (route.IsIPv4)
        {
            return AddIpv4RouteAsync(route, gateway, metric, cancellationToken, interfaceIndex, logCommand);
        }

        return AddIpv6RouteAsync(route, gateway, metric, cancellationToken, interfaceIndex, logCommand);
    }

    private Task AddIpv4RouteAsync(
        CidrRoute route,
        IPAddress? gateway,
        int metric,
        CancellationToken cancellationToken,
        int? interfaceIndex,
        bool logCommand)
    {
        if (gateway is null || gateway.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new InvalidOperationException($"IPv4 route {route.Network}/{route.PrefixLength} requires an IPv4 gateway.");
        }

        var args = $"ADD {route.Network} MASK {route.Mask} {gateway} METRIC {metric}";
        if (interfaceIndex.HasValue)
        {
            args += $" IF {interfaceIndex.Value}";
        }

        return RunCheckedAsync("route", args, cancellationToken, logCommand);
    }

    private Task AddIpv6RouteAsync(
        CidrRoute route,
        IPAddress? gateway,
        int metric,
        CancellationToken cancellationToken,
        int? interfaceIndex,
        bool logCommand)
    {
        if (gateway is not null && gateway.AddressFamily != AddressFamily.InterNetworkV6)
        {
            throw new InvalidOperationException($"IPv6 route {route.Network}/{route.PrefixLength} requires an IPv6 gateway.");
        }

        if (gateway is null && !interfaceIndex.HasValue)
        {
            throw new InvalidOperationException(
                $"IPv6 route {route.Network}/{route.PrefixLength} requires gateway or interface index.");
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
        return RunCheckedAsync("netsh", args, cancellationToken, logCommand);
    }

    public async Task<int> GetInterfaceIndexByNameAsync(string interfaceName, CancellationToken cancellationToken)
    {
        var result = await RunCommandAsync("netsh", "interface ipv4 show interfaces", cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"Failed to list interfaces: {result.Stderr}".Trim());
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

        throw new InvalidOperationException($"Interface '{interfaceName}' was not found in netsh output.");
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
                    _error.WriteLine($"[WARN] Skip deleting IPv4 route {route.Network}/{route.PrefixLength}: missing IPv4 gateway.");
                }

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
                    _error.WriteLine($"[WARN] Skip deleting IPv6 route {route.Network}/{route.PrefixLength}: invalid IPv6 gateway.");
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
                $"[WARN] Failed to delete route {route.Network}/{route.PrefixLength} via {gateway?.ToString() ?? "on-link"}: {result.Stderr}".Trim());
        }
    }

    private async Task RunCheckedAsync(string fileName, string arguments, CancellationToken cancellationToken, bool logCommand)
    {
        var result = await RunCommandAsync(fileName, arguments, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"Command failed: {fileName} {arguments}\n{result.Stderr}".Trim());
        }

        if (logCommand)
        {
            _log.WriteLine($"[INFO] {fileName} {arguments}");
        }
    }

    private static async Task<ProcessResult> RunCommandAsync(
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

        return new ProcessResult(
            process.ExitCode,
            await stdoutTask,
            await stderrTask);
    }

    private sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);
}

