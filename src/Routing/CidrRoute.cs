using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace EpTUN;

public readonly record struct CidrRoute(string Network, string Mask, int PrefixLength, AddressFamily AddressFamily)
{
    public bool IsIPv4 => AddressFamily == AddressFamily.InterNetwork;
    public bool IsIPv6 => AddressFamily == AddressFamily.InterNetworkV6;

    public static CidrRoute Parse(string cidrText)
    {
        if (string.IsNullOrWhiteSpace(cidrText))
        {
            throw new ArgumentException("CIDR cannot be empty.");
        }

        var parts = cidrText.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
        {
            throw new ArgumentException($"Invalid CIDR: {cidrText}");
        }

        if (!IPAddress.TryParse(parts[0], out var baseIp))
        {
            throw new ArgumentException($"CIDR base must be a valid IP address: {cidrText}");
        }

        if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var prefix))
        {
            throw new ArgumentException($"CIDR prefix must be numeric: {cidrText}");
        }

        return baseIp.AddressFamily switch
        {
            AddressFamily.InterNetwork => ParseIpv4(baseIp, prefix, cidrText),
            AddressFamily.InterNetworkV6 => ParseIpv6(baseIp, prefix, cidrText),
            _ => throw new ArgumentException($"Unsupported address family in CIDR: {cidrText}")
        };
    }

    private static CidrRoute ParseIpv4(IPAddress baseIp, int prefix, string cidrText)
    {
        if (prefix < 0 || prefix > 32)
        {
            throw new ArgumentException($"CIDR prefix must be between 0 and 32 for IPv4: {cidrText}");
        }

        var ipUint = ToUInt32(baseIp);
        var maskUint = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
        var networkUint = ipUint & maskUint;

        return new CidrRoute(
            Network: FromUInt32(networkUint),
            Mask: FromUInt32(maskUint),
            PrefixLength: prefix,
            AddressFamily: AddressFamily.InterNetwork);
    }

    private static CidrRoute ParseIpv6(IPAddress baseIp, int prefix, string cidrText)
    {
        if (prefix < 0 || prefix > 128)
        {
            throw new ArgumentException($"CIDR prefix must be between 0 and 128 for IPv6: {cidrText}");
        }

        Span<byte> bytes = stackalloc byte[16];
        baseIp.GetAddressBytes().CopyTo(bytes);

        var fullBytes = prefix / 8;
        var remBits = prefix % 8;

        for (var i = fullBytes + (remBits > 0 ? 1 : 0); i < bytes.Length; i++)
        {
            bytes[i] = 0;
        }

        if (remBits > 0)
        {
            var mask = (byte)(0xFF << (8 - remBits));
            bytes[fullBytes] &= mask;
        }

        return new CidrRoute(
            Network: new IPAddress(bytes).ToString(),
            Mask: string.Empty,
            PrefixLength: prefix,
            AddressFamily: AddressFamily.InterNetworkV6);
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

