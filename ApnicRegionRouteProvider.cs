using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Numerics;

namespace EpTUN;

internal static class ApnicRegionRouteProvider
{
    public static IReadOnlyList<string> LoadRegionCodes(string filePath)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("APNIC data file not found.", filePath);
        }

        var regions = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(filePath))
        {
            if (!TryParseIpEntry(line, out var countryCode, out _, out _, out _))
            {
                continue;
            }

            regions.Add(countryCode);
        }

        return regions.ToArray();
    }

    public static IReadOnlyCollection<CidrRoute> LoadCidrsForRegion(string filePath, string regionCode)
    {
        if (string.IsNullOrWhiteSpace(regionCode))
        {
            return [];
        }

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("APNIC data file not found.", filePath);
        }

        var target = regionCode.Trim().ToUpperInvariant();
        if (target.Length != 2 || !char.IsLetter(target[0]) || !char.IsLetter(target[1]))
        {
            throw new ArgumentException("Region code must be a 2-letter country code.", nameof(regionCode));
        }

        var routes = new HashSet<CidrRoute>();

        foreach (var line in File.ReadLines(filePath))
        {
            if (!TryParseIpEntry(line, out var countryCode, out var type, out var value1, out var value2) ||
                !countryCode.Equals(target, StringComparison.Ordinal))
            {
                continue;
            }

            if (type.Equals("ipv4", StringComparison.Ordinal))
            {
                if (!IPAddress.TryParse(value1, out var startIp) ||
                    startIp.AddressFamily != AddressFamily.InterNetwork ||
                    !ulong.TryParse(value2, NumberStyles.None, CultureInfo.InvariantCulture, out var count) ||
                    count == 0)
                {
                    continue;
                }

                foreach (var cidr in ExpandRangeToCidrs(startIp, count))
                {
                    routes.Add(cidr);
                }

                continue;
            }

            if (type.Equals("ipv6", StringComparison.Ordinal))
            {
                if (!IPAddress.TryParse(value1, out var startIp) ||
                    startIp.AddressFamily != AddressFamily.InterNetworkV6 ||
                    !int.TryParse(value2, NumberStyles.None, CultureInfo.InvariantCulture, out var prefix) ||
                    prefix < 0 ||
                    prefix > 128)
                {
                    continue;
                }

                routes.Add(CidrRoute.Parse($"{startIp}/{prefix}"));
            }
        }

        return routes
            .OrderBy(static x => x.IsIPv6)
            .ThenBy(static x => x.Network, StringComparer.Ordinal)
            .ThenBy(static x => x.PrefixLength)
            .ToArray();
    }

    public static IReadOnlyCollection<CidrRoute> LoadIpv4CidrsForRegion(string filePath, string regionCode)
    {
        return LoadCidrsForRegion(filePath, regionCode)
            .Where(static x => x.IsIPv4)
            .ToArray();
    }

    private static bool TryParseIpEntry(
        string line,
        out string countryCode,
        out string type,
        out string value1,
        out string value2)
    {
        countryCode = string.Empty;
        type = string.Empty;
        value1 = string.Empty;
        value2 = string.Empty;

        if (string.IsNullOrWhiteSpace(line) || line[0] == '#')
        {
            return false;
        }

        var parts = line.Split('|', StringSplitOptions.None);
        if (parts.Length < 7)
        {
            return false;
        }

        if (!parts[0].Equals("apnic", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var addrType = parts[2].ToLowerInvariant();
        if (!addrType.Equals("ipv4", StringComparison.Ordinal) &&
            !addrType.Equals("ipv6", StringComparison.Ordinal))
        {
            return false;
        }

        if (parts[1].Length != 2 ||
            !char.IsLetter(parts[1][0]) ||
            !char.IsLetter(parts[1][1]))
        {
            return false;
        }

        var status = parts[6];
        if (!status.Equals("allocated", StringComparison.OrdinalIgnoreCase) &&
            !status.Equals("assigned", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        countryCode = parts[1].ToUpperInvariant();
        type = addrType;
        value1 = parts[3];
        value2 = parts[4];
        return true;
    }

    private static IEnumerable<CidrRoute> ExpandRangeToCidrs(IPAddress startIp, ulong count)
    {
        ulong start = ToUInt32(startIp);
        var remaining = count;

        while (remaining > 0 && start <= uint.MaxValue)
        {
            var maxAlignedBlock = LowestOneBit(start);
            if (maxAlignedBlock == 0)
            {
                maxAlignedBlock = 1UL << 32;
            }

            var maxByRemaining = HighestPowerOfTwoNotGreaterThan(remaining);
            var block = Math.Min(maxAlignedBlock, maxByRemaining);

            while (block > 1 && start + block - 1 > uint.MaxValue)
            {
                block >>= 1;
            }

            var prefix = 32 - (int)BitOperations.Log2(block);
            yield return CidrRoute.Parse($"{FromUInt32((uint)start)}/{prefix}");

            start += block;
            remaining -= block;
        }
    }

    private static ulong LowestOneBit(ulong value)
    {
        return value & (0UL - value);
    }

    private static ulong HighestPowerOfTwoNotGreaterThan(ulong value)
    {
        if (value == 0)
        {
            return 0;
        }

        return 1UL << (int)BitOperations.Log2(value);
    }

    private static uint ToUInt32(IPAddress ip)
    {
        var bytes = ip.GetAddressBytes();
        return BinaryPrimitives.ReadUInt32BigEndian(bytes);
    }

    private static string FromUInt32(uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return new IPAddress(bytes).ToString();
    }
}

