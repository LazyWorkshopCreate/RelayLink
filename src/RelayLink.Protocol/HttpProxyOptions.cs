using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace RelayLink.Protocol;

public sealed record HttpProxyOptions
{
    public string PublicEntryScheme { get; init; } = "https";
    public string PeerEntryScheme { get; init; } = "http";
    public IReadOnlyList<string> PublicAllowedAuthorities { get; init; } = [];
    public IReadOnlyList<string> PublicAllowedOrigins { get; init; } = [];
    public string PeerAuthorityHost { get; init; } = "127.0.0.1";
    public IReadOnlyList<string> PeerAllowedOrigins { get; init; } = ["entry"];
    public string? CertificateRef { get; init; }
    public string? UpstreamTrustRef { get; init; }
    public string UpstreamScheme { get; init; } = "http";
    public string UpstreamAuthority { get; init; } = string.Empty;
    public string HostMode { get; init; } = "upstream";
    public string? HostValue { get; init; }
    public string OriginMode { get; init; } = "preserve";
    public string? OriginValue { get; init; }
    public long MaxRequestBodyBytes { get; init; } = 30 * 1024 * 1024;
    public int MaxRequestHeaderBytes { get; init; } = 32 * 1024;
    public int HeaderTimeoutSeconds { get; init; } = 15;
    public int ActivityTimeoutSeconds { get; init; } = 120;
    /// <summary>Idle timeout for upgraded WebSocket streams. Zero is infinite and is rejected for public ingress.</summary>
    public int WebSocketActivityTimeoutSeconds { get; init; } = HttpProxyPolicy.DefaultWebSocketActivityTimeoutSeconds;
    /// <summary>Extra request headers to drop before forwarding. The built-in client-IP and Forwarded headers are always removed.</summary>
    public IReadOnlyList<string> StripRequestHeaders { get; init; } = HttpProxyPolicy.DefaultStripRequestHeaders;
    /// <summary>Concurrent upgraded WebSocket limit. Zero uses the channel connection limit.</summary>
    public int MaxWebSocketConnections { get; init; }
}

public sealed record HttpPeerOptions(string Scheme, string AuthorityHost, string PolicyHash);

public sealed record HttpIngressContext(string AccessKind, string Scheme, string? SourceIp = null,
    string? CallerClientId = null, string? MappingId = null, string? EntryAuthority = null, string? PolicyHash = null);
public sealed record HttpChannelStatus(string ChannelId, string PolicyHash, string State);
public sealed record HttpIngressReadyMessage(Guid ConnectionId);
public sealed record HttpStatusMessage(Guid SessionId, string ConfigVersion, IReadOnlyList<HttpChannelStatus> Channels);

public static class HttpProxyPolicy
{
    public const int DefaultWebSocketActivityTimeoutSeconds = 300;
    public static readonly IReadOnlyList<string> DefaultStripRequestHeaders = ["X-Real-IP", "True-Client-IP", "X-Client-IP", "CF-Connecting-IP"];

    public static string Hash(ChannelSnapshot channel) => Convert.ToHexString(SHA256.HashData(JsonProtocolSerializer.Serialize(
        new { channel.TargetHost, channel.TargetPort, channel.MaxConnections, channel.TargetConnectTimeoutSeconds,
            channel.AuthorizedClientsOnly, channel.EndToEndEncryptionEnabled, channel.Http }))).ToLowerInvariant();

    public static string NormalizeOrigin(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim() || value.Any(char.IsControl) ||
            value.Contains('*') || value.Contains(',') || value.Contains('\\') || value.Contains('%') || value.EndsWith('/') ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            uri.Host.Length == 0 || uri.Port < 1 || uri.UserInfo.Length != 0 || uri.AbsolutePath != "/" ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("Invalid HTTP origin.");
        return uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant();
    }

    public static string NormalizeAuthority(string value, string scheme)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('/') || value.Contains('?') || value.Contains('#') || value.Contains('@'))
            throw new ArgumentException("Invalid HTTP authority.");
        return NormalizeOrigin($"{scheme}://{value}");
    }

    public static bool IsWebSocketUpgrade(IEnumerable<string>? values)
    {
        if (values is null) return false;
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            foreach (var token in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                if (token.Equals("websocket", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    public static bool OriginPermitted(bool peer, IReadOnlyList<string>? allowlist, string normalizedOrigin, string entryAuthority, bool webSocket)
    {
        if (allowlist is null || allowlist.Count == 0) return !webSocket;
        foreach (var candidate in allowlist)
        {
            if (string.Equals(candidate, "entry", StringComparison.Ordinal))
            {
                if (peer && string.Equals(normalizedOrigin, entryAuthority, StringComparison.Ordinal)) return true;
                continue;
            }
            try
            {
                if (NormalizeOrigin(candidate) == normalizedOrigin) return true;
            }
            catch (ArgumentException) { }
        }
        return false;
    }

    public static TimeSpan ActivityTimeout(bool peer, bool webSocket, int activitySeconds, int webSocketSeconds)
    {
        var seconds = webSocket ? webSocketSeconds : activitySeconds;
        if (webSocket && !peer && seconds <= 0) seconds = DefaultWebSocketActivityTimeoutSeconds;
        return seconds <= 0 ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(seconds);
    }

    public static int ResolveWebSocketConnectionCap(int maxConnections, int configured)
    {
        if (maxConnections < 1) throw new ArgumentException("Invalid channel connection limit.");
        if (configured == 0) return maxConnections;
        if (configured < 1 || configured > maxConnections)
            throw new ArgumentException("WebSocket connection cap cannot exceed the channel connection limit.");
        return configured;
    }

    public static void Validate(string applicationProtocol, string transport, bool peerOnly, bool encrypted, HttpProxyOptions? options)
    {
        if (applicationProtocol == "raw" && options is null) return;
        if (applicationProtocol != "http" || transport != "tcp" || options is null || (peerOnly && !encrypted))
            throw new ArgumentException("HTTP requires TCP, HTTP options and encrypted peer traffic.");
        if (options.PublicEntryScheme is not ("http" or "https") || options.PeerEntryScheme is not ("http" or "https") ||
            options.UpstreamScheme is not ("http" or "https") || options.HostMode is not ("upstream" or "preserve" or "fixed") ||
            options.OriginMode is not ("preserve" or "fixed") ||
            options.MaxRequestBodyBytes is < 1 or > 1024L * 1024 * 1024 || options.MaxRequestHeaderBytes is < 1024 or > 65536 ||
            options.HeaderTimeoutSeconds is < 1 or > 300 || options.ActivityTimeoutSeconds is < 1 or > 3600 ||
            options.WebSocketActivityTimeoutSeconds is < 0 or > 86400 || options.MaxWebSocketConnections is < 0 or > 100_000)
            throw new ArgumentException("Invalid HTTP settings or limits.");
        if (!peerOnly && options.WebSocketActivityTimeoutSeconds == 0)
            throw new ArgumentException("Public HTTP WebSocket activity timeout must be finite.");
        if (options.StripRequestHeaders is null || options.StripRequestHeaders.Count > 32 || options.StripRequestHeaders.Any(name => !IsHeaderName(name)))
            throw new ArgumentException("Invalid HTTP header strip list.");
        var upstream = NormalizeAuthority(options.UpstreamAuthority, options.UpstreamScheme);
        if (options.PublicAllowedAuthorities is null || options.PublicAllowedOrigins is null || options.PeerAllowedOrigins is null ||
            options.PublicAllowedAuthorities.Count > 32 || options.PublicAllowedOrigins.Count > 32 || options.PeerAllowedOrigins.Count > 32)
            throw new ArgumentException("Invalid HTTP allowlist.");
        foreach (var authority in options.PublicAllowedAuthorities) NormalizeAuthority(authority, options.PublicEntryScheme);
        foreach (var origin in options.PublicAllowedOrigins) NormalizeOrigin(origin);
        foreach (var origin in options.PeerAllowedOrigins.Where(origin => origin != "entry")) NormalizeOrigin(origin);
        if (!peerOnly && options.PublicAllowedAuthorities.Count == 0) throw new ArgumentException("Public HTTP requires allowed authorities.");
        if ((peerOnly ? options.PeerAllowedOrigins : options.PublicAllowedOrigins).Count == 0)
            throw new ArgumentException(peerOnly ? "Peer HTTP requires an origin allowlist." : "Public HTTP requires an origin allowlist.");
        var peerHostType = Uri.CheckHostName(options.PeerAuthorityHost);
        if (peerHostType is UriHostNameType.Unknown or UriHostNameType.IPv6 || options.PeerAuthorityHost.Contains('*') ||
            peerHostType == UriHostNameType.IPv4 && options.PeerAuthorityHost != "127.0.0.1")
            throw new ArgumentException("Invalid peer HTTP hostname.");
        if (options.HostMode == "fixed") NormalizeAuthority(options.HostValue ?? string.Empty, options.UpstreamScheme);
        else if (options.HostValue is not null) throw new ArgumentException("Host value requires fixed mode.");
        if (options.OriginMode == "fixed")
        {
            NormalizeOrigin(options.OriginValue ?? string.Empty);
            if ((peerOnly ? options.PeerAllowedOrigins : options.PublicAllowedOrigins).Count == 0)
                throw new ArgumentException("Origin rewrite requires a nonempty allowlist.");
        }
        else if (options.OriginValue is not null) throw new ArgumentException("Origin value requires fixed mode.");
        foreach (var reference in new[] { options.CertificateRef, options.UpstreamTrustRef }.Where(reference => reference is not null))
            if (!Regex.IsMatch(reference!, "^[a-z0-9][a-z0-9_-]{0,63}$", RegexOptions.CultureInvariant))
                throw new ArgumentException("HTTP resource references must be identifiers.");
        if ((peerOnly ? options.PeerEntryScheme : options.PublicEntryScheme) == "https" && options.CertificateRef is null)
            throw new ArgumentException("HTTPS ingress requires a certificate reference.");
        if (options.UpstreamTrustRef is not null && options.UpstreamScheme != "https")
            throw new ArgumentException("HTTP upstream cannot have TLS trust resources.");
        if (options.UpstreamScheme == "https")
        {
            if (options.HostMode == "fixed" && NormalizeAuthority(options.HostValue!, "https") != upstream)
                throw new ArgumentException("HTTPS Host must match upstream TLS authority.");
            if (options.HostMode == "preserve" && (peerOnly || options.PublicAllowedAuthorities.Any(value => NormalizeAuthority(value, "https") != upstream)))
                throw new ArgumentException("HTTPS preserved Host must match upstream TLS authority.");
        }
    }

    private static bool IsHeaderName(string? name) => name is not null && name.Length is >= 1 and <= 64 && name.All(static character => character is
        (>= '0' and <= '9') or (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or
        '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '.' or '^' or '_' or '`' or '|' or '~' or '-');
}
