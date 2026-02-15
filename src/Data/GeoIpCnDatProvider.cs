using System.Globalization;
using System.Net;
using System.Text;

namespace EpTUN;

internal static class GeoIpCnDatProvider
{
    public static IReadOnlyCollection<CidrRoute> LoadCnCidrs(string filePath)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("CN dat file not found.", filePath);
        }

        var data = File.ReadAllBytes(filePath);
        var routes = new HashSet<CidrRoute>();

        var offset = 0;
        while (offset < data.Length)
        {
            var key = ReadVarint(data, ref offset);
            var fieldNumber = (int)(key >> 3);
            var wireType = (int)(key & 0x07);

            if (fieldNumber == 1 && wireType == 2)
            {
                var length = checked((int)ReadVarint(data, ref offset));
                EnsureRemaining(data, offset, length);

                ParseGeoIpEntry(data.AsSpan(offset, length), routes);
                offset += length;
                continue;
            }

            SkipField(data, ref offset, wireType);
        }

        return routes
            .OrderBy(static x => x.IsIPv6)
            .ThenBy(static x => x.Network, StringComparer.Ordinal)
            .ThenBy(static x => x.PrefixLength)
            .ToArray();
    }

    private static void ParseGeoIpEntry(ReadOnlySpan<byte> entryData, HashSet<CidrRoute> routes)
    {
        var offset = 0;
        var countryCode = string.Empty;
        var cidrs = new List<CidrRoute>();

        while (offset < entryData.Length)
        {
            var key = ReadVarint(entryData, ref offset);
            var fieldNumber = (int)(key >> 3);
            var wireType = (int)(key & 0x07);

            switch (fieldNumber)
            {
                case 1 when wireType == 2:
                {
                    var length = checked((int)ReadVarint(entryData, ref offset));
                    EnsureRemaining(entryData, offset, length);
                    countryCode = Encoding.UTF8.GetString(entryData.Slice(offset, length)).Trim().ToUpperInvariant();
                    offset += length;
                    break;
                }
                case 2 when wireType == 2:
                {
                    var length = checked((int)ReadVarint(entryData, ref offset));
                    EnsureRemaining(entryData, offset, length);

                    if (TryParseCidr(entryData.Slice(offset, length), out var cidr))
                    {
                        cidrs.Add(cidr);
                    }

                    offset += length;
                    break;
                }
                default:
                {
                    SkipField(entryData, ref offset, wireType);
                    break;
                }
            }
        }

        if (!countryCode.Equals("CN", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        foreach (var cidr in cidrs)
        {
            routes.Add(cidr);
        }
    }

    private static bool TryParseCidr(ReadOnlySpan<byte> cidrData, out CidrRoute cidr)
    {
        cidr = default;

        var offset = 0;
        ReadOnlySpan<byte> ipBytes = ReadOnlySpan<byte>.Empty;
        int? prefix = null;

        while (offset < cidrData.Length)
        {
            var key = ReadVarint(cidrData, ref offset);
            var fieldNumber = (int)(key >> 3);
            var wireType = (int)(key & 0x07);

            switch (fieldNumber)
            {
                case 1 when wireType == 2:
                {
                    var length = checked((int)ReadVarint(cidrData, ref offset));
                    EnsureRemaining(cidrData, offset, length);
                    ipBytes = cidrData.Slice(offset, length);
                    offset += length;
                    break;
                }
                case 2 when wireType == 0:
                {
                    prefix = checked((int)ReadVarint(cidrData, ref offset));
                    break;
                }
                default:
                {
                    SkipField(cidrData, ref offset, wireType);
                    break;
                }
            }
        }

        if (ipBytes.IsEmpty || !prefix.HasValue)
        {
            return false;
        }

        if (ipBytes.Length == 4)
        {
            if (prefix.Value < 0 || prefix.Value > 32)
            {
                return false;
            }

            cidr = CidrRoute.Parse($"{new IPAddress(ipBytes)}/{prefix.Value.ToString(CultureInfo.InvariantCulture)}");
            return true;
        }

        if (ipBytes.Length == 16)
        {
            if (prefix.Value < 0 || prefix.Value > 128)
            {
                return false;
            }

            cidr = CidrRoute.Parse($"{new IPAddress(ipBytes)}/{prefix.Value.ToString(CultureInfo.InvariantCulture)}");
            return true;
        }

        return false;
    }

    private static ulong ReadVarint(ReadOnlySpan<byte> data, ref int offset)
    {
        ulong result = 0;
        var shift = 0;

        while (offset < data.Length)
        {
            var b = data[offset++];
            result |= ((ulong)(b & 0x7F)) << shift;

            if ((b & 0x80) == 0)
            {
                return result;
            }

            shift += 7;
            if (shift >= 64)
            {
                throw new InvalidDataException("Invalid varint in cn.dat.");
            }
        }

        throw new EndOfStreamException("Unexpected end of file while reading varint from cn.dat.");
    }

    private static void SkipField(ReadOnlySpan<byte> data, ref int offset, int wireType)
    {
        switch (wireType)
        {
            case 0:
                _ = ReadVarint(data, ref offset);
                return;
            case 1:
                EnsureRemaining(data, offset, 8);
                offset += 8;
                return;
            case 2:
            {
                var length = checked((int)ReadVarint(data, ref offset));
                EnsureRemaining(data, offset, length);
                offset += length;
                return;
            }
            case 5:
                EnsureRemaining(data, offset, 4);
                offset += 4;
                return;
            default:
                throw new InvalidDataException($"Unsupported protobuf wire type {wireType} in cn.dat.");
        }
    }

    private static void EnsureRemaining(ReadOnlySpan<byte> data, int offset, int count)
    {
        if (count < 0 || offset < 0 || offset + count > data.Length)
        {
            throw new InvalidDataException("cn.dat content is truncated or malformed.");
        }
    }
}

