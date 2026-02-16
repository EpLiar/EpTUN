using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EpTUN;

public sealed class AppConfig
{
    [JsonPropertyName("proxy")]
    public ProxyConfig Proxy { get; init; } = new();

    [JsonPropertyName("tun2Socks")]
    public Tun2SocksConfig Tun2Socks { get; init; } = new();

    [JsonPropertyName("vpn")]
    public VpnConfig Vpn { get; init; } = new();

    [JsonPropertyName("v2rayA")]
    public V2RayAConfig V2RayA { get; init; } = new();

    [JsonPropertyName("logging")]
    public LoggingConfig Logging { get; init; } = new();

    public static JsonSerializerOptions SerializerOptions { get; } = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public void Validate()
    {
        Proxy.Validate();
        Tun2Socks.Validate();
        Vpn.Validate();
        V2RayA.Validate();
        Logging.Validate();
    }
}

public enum LogLevelSetting
{
    Info = 1,
    Warn = 2,
    Error = 3,
    Off = 100
}

public sealed class LoggingConfig
{
    [JsonPropertyName("windowLevel")]
    public string? WindowLevel { get; init; } = "INFO";

    [JsonPropertyName("fileLevel")]
    public string? FileLevel { get; init; } = "INFO";

    public void Validate()
    {
        if (!TryParseLevel(WindowLevel, out _))
        {
            throw new ArgumentException("logging.windowLevel must be INFO, WARN, ERROR, OFF, or NONE.");
        }

        if (!TryParseLevel(FileLevel, out _))
        {
            throw new ArgumentException("logging.fileLevel must be INFO, WARN, ERROR, OFF, or NONE.");
        }
    }

    public static bool TryParseLevel(string? value, out LogLevelSetting level)
    {
        level = LogLevelSetting.Info;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        switch (value.Trim().ToUpperInvariant())
        {
            case "INFO":
                level = LogLevelSetting.Info;
                return true;
            case "WARN":
            case "WARNING":
                level = LogLevelSetting.Warn;
                return true;
            case "ERROR":
                level = LogLevelSetting.Error;
                return true;
            case "OFF":
            case "NONE":
                level = LogLevelSetting.Off;
                return true;
            default:
                return false;
        }
    }

    public static LogLevelSetting ParseLevelOrDefault(string? value, LogLevelSetting fallback = LogLevelSetting.Info)
    {
        return TryParseLevel(value, out var level) ? level : fallback;
    }

    public static string ToText(LogLevelSetting level)
    {
        return level switch
        {
            LogLevelSetting.Info => "INFO",
            LogLevelSetting.Warn => "WARN",
            LogLevelSetting.Error => "ERROR",
            LogLevelSetting.Off => "OFF",
            _ => "INFO"
        };
    }
}

public sealed class ProxyConfig
{
    [JsonPropertyName("scheme")]
    public string Scheme { get; init; } = "socks5";

    [JsonPropertyName("host")]
    public string Host { get; init; } = "127.0.0.1";

    [JsonPropertyName("port")]
    public int Port { get; init; } = 10808;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Host))
        {
            throw new ArgumentException("proxy.host is required.");
        }

        if (Port is < 1 or > 65535)
        {
            throw new ArgumentException("proxy.port must be in range 1..65535.");
        }

        if (!Scheme.Equals("socks5", StringComparison.OrdinalIgnoreCase) &&
            !Scheme.Equals("http", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("proxy.scheme must be either 'socks5' or 'http'.");
        }
    }

    public Uri BuildUri()
    {
        var hostPart = Host;
        if (IPAddress.TryParse(Host, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            hostPart = $"[{Host}]";
        }

        return new Uri($"{Scheme.ToLowerInvariant()}://{hostPart}:{Port}");
    }
}

public sealed class Tun2SocksConfig
{
    [JsonPropertyName("executablePath")]
    public string ExecutablePath { get; init; } = @".\bin\tun2socks.exe";

    [JsonPropertyName("argumentsTemplate")]
    public string ArgumentsTemplate { get; init; } =
        "-device {interfaceName} -proxy {proxyUri} -loglevel info";

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ExecutablePath))
        {
            throw new ArgumentException("tun2Socks.executablePath is required.");
        }

        if (string.IsNullOrWhiteSpace(ArgumentsTemplate))
        {
            throw new ArgumentException("tun2Socks.argumentsTemplate is required.");
        }
    }
}

public sealed class VpnConfig
{
    [JsonPropertyName("interfaceName")]
    public string InterfaceName { get; init; } = "EpTUN";

    [JsonPropertyName("tunAddress")]
    public string TunAddress { get; init; } = "10.66.66.2";

    [JsonPropertyName("tunGateway")]
    public string TunGateway { get; init; } = "10.66.66.2";

    [JsonPropertyName("tunMask")]
    public string TunMask { get; init; } = "255.255.255.0";

    [JsonPropertyName("dnsServers")]
    public string[] DnsServers { get; init; } = ["1.1.1.1", "8.8.8.8"];

    [JsonPropertyName("includeCidrs")]
    public string[] IncludeCidrs { get; init; } = ["0.0.0.0/1", "128.0.0.0/1"];

    [JsonPropertyName("excludeCidrs")]
    public string[] ExcludeCidrs { get; init; } =
    [
        "10.0.0.0/8",
        "172.16.0.0/12",
        "192.168.0.0/16",
        "169.254.0.0/16",
        "127.0.0.0/8",
        "::1/128",
        "fc00::/7",
        "fe80::/10"
    ];

    [JsonPropertyName("cnDatPath")]
    public string CnDatPath { get; init; } = @".\cn.dat";

    [JsonPropertyName("bypassCn")]
    public bool BypassCn { get; init; }

    [JsonPropertyName("routeMetric")]
    public int RouteMetric { get; init; } = 6;

    [JsonPropertyName("startupDelayMs")]
    public int StartupDelayMs { get; init; } = 1500;

    [JsonPropertyName("defaultGatewayOverride")]
    public string? DefaultGatewayOverride { get; init; }

    [JsonPropertyName("addBypassRouteForProxyHost")]
    public bool AddBypassRouteForProxyHost { get; init; } = true;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(InterfaceName))
        {
            throw new ArgumentException("vpn.interfaceName is required.");
        }

        if (string.IsNullOrWhiteSpace(CnDatPath))
        {
            throw new ArgumentException("vpn.cnDatPath is required.");
        }

        EnsureIPv4(TunAddress, "vpn.tunAddress");
        EnsureIPv4(TunGateway, "vpn.tunGateway");
        EnsureIPv4(TunMask, "vpn.tunMask");

        foreach (var dns in DnsServers)
        {
            EnsureIPv4(dns, "vpn.dnsServers");
        }

        foreach (var include in IncludeCidrs)
        {
            _ = CidrRoute.Parse(include);
        }

        foreach (var exclude in ExcludeCidrs)
        {
            _ = CidrRoute.Parse(exclude);
        }

        if (RouteMetric < 1)
        {
            throw new ArgumentException("vpn.routeMetric must be >= 1.");
        }

        if (StartupDelayMs < 0)
        {
            throw new ArgumentException("vpn.startupDelayMs must be >= 0.");
        }

        if (!string.IsNullOrWhiteSpace(DefaultGatewayOverride))
        {
            EnsureIPv4(DefaultGatewayOverride, "vpn.defaultGatewayOverride");
        }
    }

    private static void EnsureIPv4(string value, string fieldName)
    {
        if (!IPAddress.TryParse(value, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            throw new ArgumentException($"{fieldName} must be a valid IPv4 address.");
        }
    }
}

public sealed class V2RayAConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }

    [JsonPropertyName("baseUrl")]
    public string BaseUrl { get; init; } = "http://localhost:2017/";

    [JsonPropertyName("authorization")]
    public string? Authorization { get; init; }

    [JsonPropertyName("username")]
    public string? Username { get; init; }

    [JsonPropertyName("password")]
    public string? Password { get; init; }

    [JsonPropertyName("requestId")]
    public string? RequestId { get; init; }

    [JsonPropertyName("timeoutMs")]
    public int TimeoutMs { get; init; } = 5000;

    [JsonPropertyName("resolveHostnames")]
    public bool ResolveHostnames { get; init; } = true;

    [JsonPropertyName("autoDetectProxyPort")]
    public bool AutoDetectProxyPort { get; init; } = true;

    [JsonPropertyName("preferPacPort")]
    public bool PreferPacPort { get; init; } = false;

    [JsonPropertyName("proxyHostOverride")]
    public string? ProxyHostOverride { get; init; }

    public void Validate()
    {
        if (!Enabled)
        {
            return;
        }

        _ = BuildBaseUri();

        var hasAuthorization = !string.IsNullOrWhiteSpace(Authorization);
        var hasUsername = !string.IsNullOrWhiteSpace(Username);
        var hasPassword = !string.IsNullOrWhiteSpace(Password);

        if (hasUsername != hasPassword)
        {
            throw new ArgumentException("v2rayA.username and v2rayA.password must both be set together.");
        }

        if (!hasAuthorization && !hasUsername)
        {
            throw new ArgumentException(
                "When v2rayA.enabled is true, set either v2rayA.authorization or both v2rayA.username and v2rayA.password.");
        }

        if (TimeoutMs is < 100 or > 120000)
        {
            throw new ArgumentException("v2rayA.timeoutMs must be in range 100..120000.");
        }
    }

    public bool HasCredentials()
    {
        return !string.IsNullOrWhiteSpace(Username) &&
               !string.IsNullOrWhiteSpace(Password);
    }

    public Uri BuildBaseUri()
    {
        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("v2rayA.baseUrl must be an absolute http/https URL.");
        }

        var normalized = uri.AbsoluteUri;
        if (!normalized.EndsWith("/", StringComparison.Ordinal))
        {
            normalized += "/";
        }

        return new Uri(normalized, UriKind.Absolute);
    }
}

