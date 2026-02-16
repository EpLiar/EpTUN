using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EpTUN;

internal static class V2RayATouchClient
{
    private static readonly ConcurrentDictionary<string, V2RayASessionState> SessionStates = new(StringComparer.Ordinal);
    private static readonly TimeSpan CookieSessionReuseWindow = TimeSpan.FromMinutes(10);

    public static async Task<Uri> ResolveProxyUriAsync(
        V2RayAConfig config,
        ProxyConfig fallbackProxy,
        TextWriter log,
        CancellationToken cancellationToken)
    {
        var baseUri = config.BuildBaseUri();
        var portsUri = new Uri(baseUri, "api/ports");

        var sessionState = GetSessionState(config, baseUri);
        using var httpClient = CreateHttpClient(config, sessionState);
        var runtimeAuthorization = await ResolveAuthorizationAsync(config, baseUri, httpClient, log, cancellationToken, sessionState);
        using var request = BuildRequest(config, baseUri, portsUri, runtimeAuthorization);

        log.WriteLine($"[INFO] Querying v2rayA: {portsUri}");
        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"v2rayA /api/ports returned {(int)response.StatusCode} {response.ReasonPhrase}: {TrimForLog(responseText)}");
        }

        using var doc = JsonDocument.Parse(responseText);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("v2rayA /api/ports response does not contain data.");
        }

        var (primaryKey, fallbackKey) = ResolvePortKeys(fallbackProxy.Scheme, config.PreferPacPort);
        var primaryPort = ReadPort(data, primaryKey);
        var fallbackPort = ReadPort(data, fallbackKey);

        if ((!primaryPort.HasValue || primaryPort.Value <= 0) &&
            (!fallbackPort.HasValue || fallbackPort.Value <= 0))
        {
            throw new InvalidOperationException(
                $"v2rayA /api/ports did not provide a valid {primaryKey}/{fallbackKey} port.");
        }

        var host = string.IsNullOrWhiteSpace(config.ProxyHostOverride)
            ? fallbackProxy.Host
            : config.ProxyHostOverride.Trim();

        if (string.IsNullOrWhiteSpace(host))
        {
            host = baseUri.Host;
        }

        var candidates = new List<(string Key, Uri ProxyUri)>();
        AddCandidate(candidates, primaryKey, primaryPort, fallbackProxy.Scheme, host);
        AddCandidate(candidates, fallbackKey, fallbackPort, fallbackProxy.Scheme, host);

        foreach (var candidate in candidates)
        {
            if (await IsProxyEndpointReachableAsync(candidate.ProxyUri, config.TimeoutMs, cancellationToken))
            {
                log.WriteLine($"[INFO] v2rayA proxy endpoint selected: {candidate.ProxyUri} ({candidate.Key}, reachable)");
                return candidate.ProxyUri;
            }

            log.WriteLine($"[WARN] v2rayA proxy endpoint not reachable: {candidate.ProxyUri} ({candidate.Key})");
        }

        var fallbackCandidate = candidates[0];
        log.WriteLine($"[WARN] No v2rayA proxy endpoint is reachable; fallback to {fallbackCandidate.ProxyUri} ({fallbackCandidate.Key}).");
        return fallbackCandidate.ProxyUri;
    }

    public static async Task<IReadOnlyCollection<CidrRoute>> ResolveExcludeCidrsAsync(
        V2RayAConfig config,
        TextWriter log,
        CancellationToken cancellationToken)
    {
        var baseUri = config.BuildBaseUri();
        var touchUri = new Uri(baseUri, "api/touch");

        var sessionState = GetSessionState(config, baseUri);
        using var httpClient = CreateHttpClient(config, sessionState);
        var runtimeAuthorization = await ResolveAuthorizationAsync(config, baseUri, httpClient, log, cancellationToken, sessionState);
        using var request = BuildRequest(config, baseUri, touchUri, runtimeAuthorization);

        log.WriteLine($"[INFO] Querying v2rayA: {touchUri}");
        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"v2rayA /api/touch returned {(int)response.StatusCode} {response.ReasonPhrase}: {TrimForLog(responseText)}");
        }

        using var doc = JsonDocument.Parse(responseText);
        if (!doc.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("touch", out var touch))
        {
            throw new InvalidOperationException("v2rayA /api/touch response does not contain data.touch.");
        }

        var connected = EnumerateConnected(touch).ToArray();
        if (connected.Length == 0)
        {
            log.WriteLine("[INFO] v2rayA reported no connected server. No dynamic excludeCidrs added.");
            return [];
        }

        var rawAddresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var endpoint in connected)
        {
            if (TryGetAddressBySubscription(touch, endpoint.Sub, endpoint.Id, out var address) ||
                TryGetAddressByIdAcrossSources(touch, endpoint.Id, out address))
            {
                rawAddresses.Add(address);
            }
        }

        if (rawAddresses.Count == 0)
        {
            log.WriteLine("[WARN] v2rayA connected server found, but address mapping failed. No dynamic excludeCidrs added.");
            return [];
        }

        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var address in rawAddresses)
        {
            if (TryExtractHost(address, out var host))
            {
                hosts.Add(host);
            }
        }

        var cidrs = new HashSet<CidrRoute>();
        foreach (var host in hosts)
        {
            if (IPAddress.TryParse(host, out var ip))
            {
                if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                {
                    cidrs.Add(CidrRoute.Parse($"{ip}/32"));
                }
                else if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
                {
                    cidrs.Add(CidrRoute.Parse($"{ip}/128"));
                }

                continue;
            }

            if (!config.ResolveHostnames)
            {
                continue;
            }

            IPAddress[] resolved;
            try
            {
                resolved = await Dns.GetHostAddressesAsync(host, cancellationToken);
            }
            catch
            {
                continue;
            }

            foreach (var address in resolved)
            {
                if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                {
                    cidrs.Add(CidrRoute.Parse($"{address}/32"));
                }
                else if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
                {
                    cidrs.Add(CidrRoute.Parse($"{address}/128"));
                }
            }
        }

        if (cidrs.Count == 0)
        {
            log.WriteLine("[WARN] v2rayA connected servers resolved to no IP addresses. No dynamic excludeCidrs added.");
            return [];
        }

        var ordered = cidrs
            .OrderBy(static x => x.IsIPv6)
            .ThenBy(static x => x.Network, StringComparer.Ordinal)
            .ThenBy(static x => x.PrefixLength)
            .ToArray();

        const int previewCount = 20;
        var sample = ordered
            .Take(previewCount)
            .Select(static x => x.Network + "/" + x.PrefixLength)
            .ToArray();
        var omitted = ordered.Length - sample.Length;
        if (omitted > 0)
        {
            log.WriteLine(
                $"[INFO] v2rayA dynamic excludeCidrs resolved {ordered.Length} entries. Sample: {string.Join(", ", sample)} (+{omitted} more)");
        }
        else
        {
            log.WriteLine($"[INFO] v2rayA dynamic excludeCidrs: {string.Join(", ", sample)}");
        }

        return ordered;
    }

    private static HttpClient CreateHttpClient(V2RayAConfig config, V2RayASessionState sessionState)
    {
        var handler = new HttpClientHandler
        {
            UseCookies = true,
            CookieContainer = sessionState.CookieContainer
        };

        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMilliseconds(config.TimeoutMs)
        };
    }

    private static async Task<string?> ResolveAuthorizationAsync(
        V2RayAConfig config,
        Uri baseUri,
        HttpClient httpClient,
        TextWriter log,
        CancellationToken cancellationToken,
        V2RayASessionState sessionState)
    {
        var configuredAuthorization = NormalizeAuthorization(config.Authorization);
        if (!config.HasCredentials())
        {
            return configuredAuthorization;
        }

        lock (sessionState.SyncRoot)
        {
            if (!string.IsNullOrWhiteSpace(sessionState.Authorization))
            {
                return sessionState.Authorization;
            }

            if (sessionState.CookieSessionReady &&
                DateTimeOffset.UtcNow - sessionState.LastLoginUtc <= CookieSessionReuseWindow)
            {
                log.WriteLine("[INFO] v2rayA login skipped (reusing previous cookie session).");
                return configuredAuthorization;
            }
        }

        var loginUri = new Uri(baseUri, "api/login");
        using var loginRequest = BuildLoginRequest(config, baseUri, loginUri);
        log.WriteLine($"[INFO] Logging in to v2rayA: {loginUri}");

        using var loginResponse = await httpClient.SendAsync(
            loginRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        var responseText = await loginResponse.Content.ReadAsStringAsync(cancellationToken);
        if (!loginResponse.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"v2rayA /api/login returned {(int)loginResponse.StatusCode} {loginResponse.ReasonPhrase}: {TrimForLog(responseText)}");
        }

        ValidateLoginResponse(responseText);

        var loginAuthorization = ExtractAuthorizationFromLoginResponse(loginResponse, responseText);
        if (!string.IsNullOrWhiteSpace(loginAuthorization))
        {
            lock (sessionState.SyncRoot)
            {
                sessionState.Authorization = loginAuthorization;
                sessionState.CookieSessionReady = false;
                sessionState.LastLoginUtc = DateTimeOffset.UtcNow;
            }

            log.WriteLine("[INFO] v2rayA login succeeded (authorization token acquired).");
            return loginAuthorization;
        }

        lock (sessionState.SyncRoot)
        {
            sessionState.CookieSessionReady = true;
            sessionState.LastLoginUtc = DateTimeOffset.UtcNow;
        }

        if (!string.IsNullOrWhiteSpace(configuredAuthorization))
        {
            log.WriteLine("[WARN] v2rayA login succeeded but no authorization token found; fallback to configured authorization.");
            return configuredAuthorization;
        }

        log.WriteLine("[INFO] v2rayA login succeeded (session-cookie mode).");
        return null;
    }

    private static HttpRequestMessage BuildLoginRequest(V2RayAConfig config, Uri baseUri, Uri loginUri)
    {
        if (string.IsNullOrWhiteSpace(config.Username) || string.IsNullOrWhiteSpace(config.Password))
        {
            throw new InvalidOperationException("v2rayA.username and v2rayA.password are required for /api/login.");
        }

        var payloadJson = JsonSerializer.Serialize(
            new V2RayALoginPayload(config.Username.Trim(), config.Password));

        var request = new HttpRequestMessage(HttpMethod.Post, loginUri);
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        request.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8,ja;q=0.7");
        request.Headers.TryAddWithoutValidation("Origin", baseUri.GetLeftPart(UriPartial.Authority));
        request.Headers.TryAddWithoutValidation("Referer", baseUri.ToString());
        request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 EpTUN");
        request.Headers.TryAddWithoutValidation("X-V2raya-Request-Id", BuildRequestId(config.RequestId));
        request.Content = new StringContent(payloadJson, Encoding.UTF8, "application/json");
        return request;
    }

    private static void ValidateLoginResponse(string responseText)
    {
        if (string.IsNullOrWhiteSpace(responseText))
        {
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(responseText);
            if (!TryReadStringPropertyIgnoreCase(doc.RootElement, "code", out var code))
            {
                return;
            }

            if (code.Equals("SUCCESS", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (TryReadStringPropertyIgnoreCase(doc.RootElement, "message", out var message))
            {
                throw new InvalidOperationException($"v2rayA /api/login failed: {message} (code: {code}).");
            }

            throw new InvalidOperationException($"v2rayA /api/login failed with code: {code}.");
        }
        catch (JsonException)
        {
            // Non-JSON response is handled by caller; keep behavior compatible.
        }
    }

    private static HttpRequestMessage BuildRequest(
        V2RayAConfig config,
        Uri baseUri,
        Uri requestUri,
        string? runtimeAuthorization)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        request.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8,ja;q=0.7");

        var authorization = NormalizeAuthorization(runtimeAuthorization);
        if (!string.IsNullOrWhiteSpace(authorization))
        {
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        }

        request.Headers.TryAddWithoutValidation("Referer", baseUri.ToString());
        request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 EpTUN");
        request.Headers.TryAddWithoutValidation("X-V2raya-Request-Id", BuildRequestId(config.RequestId));
        return request;
    }

    private static string? ExtractAuthorizationFromLoginResponse(HttpResponseMessage response, string responseText)
    {
        if (TryReadAuthorizationFromHeaders(response, out var headerAuthorization))
        {
            return headerAuthorization;
        }

        if (string.IsNullOrWhiteSpace(responseText))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(responseText);
            if (TryReadAuthorizationFromJson(doc.RootElement, out var jsonAuthorization))
            {
                return jsonAuthorization;
            }
        }
        catch
        {
            // Keep cookie-session mode if login body is not JSON.
        }

        return null;
    }

    private static bool TryReadAuthorizationFromHeaders(HttpResponseMessage response, out string authorization)
    {
        authorization = string.Empty;
        if (response.Headers.TryGetValues("Authorization", out var values))
        {
            var value = values.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(value))
            {
                authorization = value.Trim();
                return true;
            }
        }

        return false;
    }

    private static bool TryReadAuthorizationFromJson(JsonElement element, out string authorization)
    {
        authorization = string.Empty;
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (TryReadStringByCandidateNames(element, out authorization))
        {
            return true;
        }

        if (TryReadPropertyIgnoreCase(element, "data", out var data) &&
            data.ValueKind == JsonValueKind.Object &&
            TryReadStringByCandidateNames(data, out authorization))
        {
            return true;
        }

        return false;
    }

    private static bool TryReadPropertyIgnoreCase(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.NameEquals(name) ||
                    property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static bool TryReadStringPropertyIgnoreCase(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        if (!TryReadPropertyIgnoreCase(element, name, out var property))
        {
            return false;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var text = property.GetString();
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        value = text.Trim();
        return true;
    }

    private static bool TryReadStringByCandidateNames(JsonElement element, out string value)
    {
        foreach (var candidate in new[] { "authorization", "token", "accessToken", "access_token", "auth" })
        {
            if (TryReadStringPropertyIgnoreCase(element, candidate, out value))
            {
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    private static string? NormalizeAuthorization(string? authorization)
    {
        return string.IsNullOrWhiteSpace(authorization) ? null : authorization.Trim();
    }

    private static V2RayASessionState GetSessionState(V2RayAConfig config, Uri baseUri)
    {
        var key =
            $"{baseUri.Scheme}://{baseUri.Authority}|" +
            $"{NormalizeAuthorization(config.Authorization) ?? string.Empty}|" +
            $"{config.Username ?? string.Empty}|" +
            $"{config.Password ?? string.Empty}";

        return SessionStates.GetOrAdd(key, static _ => new V2RayASessionState());
    }

    private static string BuildRequestId(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        return Convert.ToBase64String(Guid.NewGuid().ToByteArray())
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    private static (string Primary, string Secondary) ResolvePortKeys(string scheme, bool preferPac)
    {
        var isHttp = scheme.Equals("http", StringComparison.OrdinalIgnoreCase);
        if (isHttp)
        {
            return preferPac ? ("httpWithPac", "http") : ("http", "httpWithPac");
        }

        return preferPac ? ("socks5WithPac", "socks5") : ("socks5", "socks5WithPac");
    }

    private static int? ReadPort(JsonElement data, string key)
    {
        if (!data.TryGetProperty(key, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number)
        {
            if (value.TryGetInt32(out var port))
            {
                return port;
            }

            return null;
        }

        if (value.ValueKind == JsonValueKind.String &&
            int.TryParse(value.GetString(), out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static void AddCandidate(
        List<(string Key, Uri ProxyUri)> candidates,
        string key,
        int? port,
        string scheme,
        string host)
    {
        if (!port.HasValue || port.Value <= 0)
        {
            return;
        }

        var uri = BuildProxyUri(scheme, host, port.Value);
        foreach (var item in candidates)
        {
            if (item.ProxyUri == uri)
            {
                return;
            }
        }

        candidates.Add((key, uri));
    }

    private static async Task<bool> IsProxyEndpointReachableAsync(
        Uri proxyUri,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        var probeTimeoutMs = Math.Clamp(timeoutMs / 2, 300, 3000);

        try
        {
            using var tcpClient = new TcpClient();
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linkedCts.CancelAfter(probeTimeoutMs);
            await tcpClient.ConnectAsync(proxyUri.Host, proxyUri.Port, linkedCts.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }
    private static Uri BuildProxyUri(string scheme, string host, int port)
    {
        var normalizedHost = host.Trim();
        if (normalizedHost.StartsWith("[", StringComparison.Ordinal) &&
            normalizedHost.EndsWith("]", StringComparison.Ordinal) &&
            normalizedHost.Length > 2)
        {
            normalizedHost = normalizedHost[1..^1];
        }

        if (normalizedHost.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            normalizedHost = "127.0.0.1";
        }

        var hostPart = normalizedHost;
        if (IPAddress.TryParse(normalizedHost, out var ip) &&
            ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            hostPart = $"[{normalizedHost}]";
        }

        return new Uri($"{scheme.ToLowerInvariant()}://{hostPart}:{port}");
    }

    private static IEnumerable<(int Id, int Sub)> EnumerateConnected(JsonElement touch)
    {
        if (!touch.TryGetProperty("connectedServer", out var connected) ||
            connected.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var item in connected.EnumerateArray())
        {
            if (!TryReadInt(item, "id", out var id))
            {
                continue;
            }

            var sub = 0;
            _ = TryReadInt(item, "sub", out sub);
            yield return (id, sub);
        }
    }

    private static bool TryGetAddressBySubscription(JsonElement touch, int sub, int id, out string address)
    {
        address = string.Empty;

        if (!touch.TryGetProperty("subscriptions", out var subscriptions) ||
            subscriptions.ValueKind != JsonValueKind.Array ||
            sub < 0 ||
            sub >= subscriptions.GetArrayLength())
        {
            return false;
        }

        var subscription = subscriptions[sub];
        if (!subscription.TryGetProperty("servers", out var servers) ||
            servers.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var server in servers.EnumerateArray())
        {
            if (!TryReadInt(server, "id", out var serverId) || serverId != id)
            {
                continue;
            }

            if (server.TryGetProperty("address", out var addressProperty) &&
                addressProperty.ValueKind == JsonValueKind.String)
            {
                address = addressProperty.GetString() ?? string.Empty;
                return !string.IsNullOrWhiteSpace(address);
            }
        }

        return false;
    }

    private static bool TryGetAddressByIdAcrossSources(JsonElement touch, int id, out string address)
    {
        address = string.Empty;

        if (touch.TryGetProperty("subscriptions", out var subscriptions) &&
            subscriptions.ValueKind == JsonValueKind.Array)
        {
            foreach (var subscription in subscriptions.EnumerateArray())
            {
                if (!subscription.TryGetProperty("servers", out var servers) ||
                    servers.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var server in servers.EnumerateArray())
                {
                    if (!TryReadInt(server, "id", out var serverId) || serverId != id)
                    {
                        continue;
                    }

                    if (server.TryGetProperty("address", out var addressProperty) &&
                        addressProperty.ValueKind == JsonValueKind.String)
                    {
                        address = addressProperty.GetString() ?? string.Empty;
                        return !string.IsNullOrWhiteSpace(address);
                    }
                }
            }
        }

        if (touch.TryGetProperty("servers", out var directServers) &&
            directServers.ValueKind == JsonValueKind.Array)
        {
            foreach (var server in directServers.EnumerateArray())
            {
                if (!TryReadInt(server, "id", out var serverId) || serverId != id)
                {
                    continue;
                }

                if (server.TryGetProperty("address", out var addressProperty) &&
                    addressProperty.ValueKind == JsonValueKind.String)
                {
                    address = addressProperty.GetString() ?? string.Empty;
                    return !string.IsNullOrWhiteSpace(address);
                }
            }
        }

        return false;
    }

    private static bool TryReadInt(JsonElement element, string propertyName, out int value)
    {
        value = default;

        if (!element.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.Number)
        {
            return property.TryGetInt32(out value);
        }

        if (property.ValueKind == JsonValueKind.String)
        {
            return int.TryParse(property.GetString(), out value);
        }

        return false;
    }

    private static bool TryExtractHost(string address, out string host)
    {
        host = string.Empty;
        if (string.IsNullOrWhiteSpace(address))
        {
            return false;
        }

        var normalized = address.Trim();
        if (!normalized.Contains("://", StringComparison.Ordinal))
        {
            normalized = "tcp://" + normalized;
        }

        if (Uri.TryCreate(normalized, UriKind.Absolute, out var uri) &&
            !string.IsNullOrWhiteSpace(uri.Host))
        {
            host = uri.Host;
            return true;
        }

        if (IPAddress.TryParse(address.Trim(), out var ip))
        {
            host = ip.ToString();
            return true;
        }

        return false;
    }

    private static string TrimForLog(string text)
    {
        const int maxLen = 400;
        if (text.Length <= maxLen)
        {
            return text;
        }

        return text[..maxLen] + "...";
    }

    private sealed record V2RayALoginPayload(
        [property: JsonPropertyName("username")] string Username,
        [property: JsonPropertyName("password")] string Password);

    private sealed class V2RayASessionState
    {
        public object SyncRoot { get; } = new();
        public CookieContainer CookieContainer { get; } = new();
        public string? Authorization { get; set; }
        public bool CookieSessionReady { get; set; }
        public DateTimeOffset LastLoginUtc { get; set; }
    }
}












