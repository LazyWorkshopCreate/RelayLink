using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RelayLink.Protocol;
using Yarp.ReverseProxy.Forwarder;

namespace RelayLink.Agent;

/// <summary>Per-profile HTTP hosts. Policies and certificate resources remain frozen until this profile worker is recreated.</summary>
internal sealed class HttpProxyRuntime(AgentConfiguration configuration, ILogger? logger = null) : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly SemaphoreSlim prepareGate = new(1, 1);
    private readonly Dictionary<string, (string Hash, HttpProxyHost? Host)> hosts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Hash, bool Revoked)> mappings = new(StringComparer.Ordinal);
    private bool initialized;
    private IReadOnlyList<HttpChannelStatus> states = [];
    public IReadOnlyList<HttpChannelStatus> States { get { lock (gate) return states; } }
    internal int ActiveHostCount { get { lock (gate) return hosts.Count(pair => pair.Value.Host is { IsDisposed: false }); } }

    public async Task PrepareAsync(ClientConfigSnapshot snapshot, CancellationToken token)
    {
        await prepareGate.WaitAsync(token);
        var created = new List<HttpProxyHost>();
        try
        {
            List<ChannelSnapshot> toCreate;
            lock (gate)
            {
                if (!initialized)
                    foreach (var mapping in snapshot.OutboundMappings.Where(mapping => mapping.Enabled && mapping.ApplicationProtocol == "http"))
                        mappings[mapping.MappingId] = (MappingHash(mapping), false);
                foreach (var id in mappings.Keys.ToArray())
                    if (!snapshot.OutboundMappings.Any(mapping => mapping.Enabled && mapping.MappingId == id && mapping.ApplicationProtocol == "http" && MappingHash(mapping) == mappings[id].Hash))
                        mappings[id] = (mappings[id].Hash, true);
                toCreate = initialized ? [] : HttpChannels(snapshot).Where(channel => !hosts.ContainsKey(channel.ChannelId)).ToList();
            }

            var newHosts = new Dictionary<string, (string Hash, HttpProxyHost? Host)>(StringComparer.Ordinal);
            foreach (var channel in toCreate)
            {
                token.ThrowIfCancellationRequested();
                HttpProxyHost? host = null;
                try
                {
                    host = await HttpProxyHost.CreateAsync(channel, Path.Combine(Path.GetDirectoryName(configuration.E2eIdentityPath)!, "http-resources"), token, logger);
                    created.Add(host);
                }
                catch (Exception exception) when (exception is IOException or ArgumentException or System.Security.Cryptography.CryptographicException or InvalidOperationException)
                { /* Report unavailable without exposing resource paths or credentials. */ }
                newHosts[channel.ChannelId] = (HttpProxyPolicy.Hash(channel), host);
            }

            HttpProxyHost[] retiring;
            lock (gate)
            {
                foreach (var pair in newHosts) hosts[pair.Key] = pair.Value;
                created.Clear();
                var result = new List<HttpChannelStatus>();
                var retire = new List<HttpProxyHost>();
                foreach (var channel in HttpChannels(snapshot))
                {
                    var hash = HttpProxyPolicy.Hash(channel);
                    hosts.TryGetValue(channel.ChannelId, out var current);
                    var unavailable = current.Host is { Revoked: true } || current.Host is { IsDisposed: true };
                    var state = current.Hash != hash || unavailable ? "restart_required" : current.Host is null ? "resource_unavailable" : "ready";
                    if (state != "ready" && current.Host is not null)
                    {
                        retire.Add(current.Host);
                        hosts.Remove(channel.ChannelId);
                    }
                    result.Add(new HttpChannelStatus(channel.ChannelId, hash, state));
                    logger?.LogInformation("HTTP channel {ChannelId} runtime state: {HttpState}.", channel.ChannelId, state);
                }
                foreach (var removed in hosts.Keys.Where(id => !snapshot.Channels.Any(channel => channel.Enabled && channel.ChannelId == id && channel.ApplicationProtocol == "http")).ToArray())
                {
                    if (hosts[removed].Host is { } host) retire.Add(host);
                    hosts.Remove(removed);
                }
                initialized = true;
                states = result;
                retiring = retire.Distinct().ToArray();
            }

            Exception? disposeFailure = null;
            foreach (var host in retiring)
            {
                try { await host.DisposeAsync(); }
                catch (Exception exception) { disposeFailure ??= exception; }
            }
            if (disposeFailure is not null) throw disposeFailure;
        }
        catch
        {
            foreach (var host in created) await host.DisposeAsync();
            throw;
        }
        finally { prepareGate.Release(); }
    }

    private static IEnumerable<ChannelSnapshot> HttpChannels(ClientConfigSnapshot snapshot) =>
        snapshot.Channels.Where(channel => channel.Enabled && channel.ApplicationProtocol == "http");

    private static string MappingHash(OutboundMappingSnapshot mapping) => Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(JsonProtocolSerializer.Serialize(mapping)));

    public string? MappingState(OutboundMappingSnapshot mapping)
    {
        lock (gate) return MappingStateLocked(mapping);
    }

    private string? MappingStateLocked(OutboundMappingSnapshot mapping) => mapping.ApplicationProtocol != "http" ? null :
        mappings.TryGetValue(mapping.MappingId, out var frozen) && !frozen.Revoked && frozen.Hash == MappingHash(mapping) ? "ready" : "restart_required";

    public IReadOnlyDictionary<string, string> MappingStates(ClientConfigSnapshot snapshot)
    {
        lock (gate) return snapshot.OutboundMappings.Where(mapping => mapping.ApplicationProtocol == "http").ToDictionary(mapping => mapping.MappingId,
            mapping => mapping.Enabled ? MappingStateLocked(mapping)! : "disabled", StringComparer.Ordinal);
    }

    public ClientConfigSnapshot EffectivePeerConfiguration(ClientConfigSnapshot snapshot)
    {
        lock (gate) return snapshot with
        {
            OutboundMappings = snapshot.OutboundMappings.Select(mapping => mapping.ApplicationProtocol == "http" && MappingStateLocked(mapping) != "ready"
                ? mapping with { Enabled = false } : mapping).ToArray()
        };
    }

    public bool IsReady(ChannelSnapshot channel)
    {
        lock (gate) return IsReadyLocked(channel);
    }

    private bool IsReadyLocked(ChannelSnapshot channel) => hosts.TryGetValue(channel.ChannelId, out var value) &&
        value.Host is { Revoked: false, IsDisposed: false } && value.Hash == HttpProxyPolicy.Hash(channel);

    public async Task ServeAsync(ChannelSnapshot channel, Stream stream, HttpIngressContext context, CancellationToken token, Guid? connectionId = null)
    {
        HttpProxyHost host;
        lock (gate)
        {
            if (!IsReadyLocked(channel) || context.PolicyHash != HttpProxyPolicy.Hash(channel) ||
                context.AccessKind != (channel.AuthorizedClientsOnly ? "peer" : "public") ||
                context.Scheme != (channel.AuthorizedClientsOnly ? channel.Http!.PeerEntryScheme : channel.Http!.PublicEntryScheme))
                throw new ProtocolException("HTTP ingress is unavailable or mismatched.");
            host = hosts[channel.ChannelId].Host!;
        }
        try { await host.ServeAsync(stream, context, token, connectionId); }
        catch (ObjectDisposedException) { throw new ProtocolException("HTTP ingress is unavailable or mismatched."); }
    }

    public async ValueTask DisposeAsync()
    {
        await prepareGate.WaitAsync();
        try
        {
            HttpProxyHost[] retiring;
            lock (gate)
            {
                retiring = hosts.Values.Select(value => value.Host).Where(host => host is not null).Cast<HttpProxyHost>().ToArray();
                hosts.Clear();
                states = [];
            }
            Exception? disposeFailure = null;
            foreach (var host in retiring)
            {
                try { await host.DisposeAsync(); }
                catch (Exception exception) { disposeFailure ??= exception; }
            }
            if (disposeFailure is not null) throw disposeFailure;
        }
        finally { prepareGate.Release(); }
    }
}

internal sealed class HttpProxyHost : IAsyncDisposable
{
    private readonly ChannelSnapshot channel;
    private readonly HttpProxyOptions options;
    private readonly VirtualHttpTransport transport;
    private readonly WebApplication app;
    private readonly HttpMessageInvoker client;
    private readonly SemaphoreSlim requests;
    private readonly SemaphoreSlim websockets;
    private readonly X509Certificate2? certificate;
    private readonly X509Certificate2Collection? ingressChain;
    private readonly X509Certificate2Collection? roots;
    private readonly CancellationTokenSource lifetime = new();
    private int disposeStarted;
    private int disposed;
    public bool Revoked
    {
        get
        {
            try { return lifetime.IsCancellationRequested; }
            catch (ObjectDisposedException) { return true; }
        }
    }
    internal bool IsDisposed => Volatile.Read(ref disposed) == 1;

    private HttpProxyHost(ChannelSnapshot channel, VirtualHttpTransport transport, WebApplication app, HttpMessageInvoker client,
        X509Certificate2? certificate, X509Certificate2Collection? roots, X509Certificate2Collection? ingressChain)
    {
        this.channel = channel; options = channel.Http!; this.transport = transport; this.app = app; this.client = client;
        this.certificate = certificate; this.roots = roots; requests = new(channel.MaxConnections);
        websockets = new(HttpProxyPolicy.ResolveWebSocketConnectionCap(channel.MaxConnections, options.MaxWebSocketConnections));
        this.ingressChain = ingressChain;
    }

    public static async Task<HttpProxyHost> CreateAsync(ChannelSnapshot channel, string resources, CancellationToken token, ILogger? logger = null)
    {
        var options = channel.Http!;
        HttpProxyPolicy.Validate(channel.ApplicationProtocol, channel.TransportProtocol, channel.AuthorizedClientsOnly, channel.EndToEndEncryptionEnabled, options);
        X509Certificate2? certificate = null;
        X509Certificate2Collection? ingressChain = null;
        X509Certificate2Collection? roots = null;
        HttpMessageInvoker? client = null;
        WebApplication? app = null;
        try
        {
            var scheme = channel.AuthorizedClientsOnly ? options.PeerEntryScheme : options.PublicEntryScheme;
            if (scheme == "https")
            {
                certificate = X509Certificate2.CreateFromPemFile(Path.Combine(resources, options.CertificateRef + ".crt.pem"), Path.Combine(resources, options.CertificateRef + ".key.pem"));
                ingressChain = new X509Certificate2Collection();
                ingressChain.ImportFromPemFile(Path.Combine(resources, options.CertificateRef + ".crt.pem"));
                foreach (var chainCertificate in ingressChain.Cast<X509Certificate2>().ToArray())
                    if (chainCertificate.RawData.AsSpan().SequenceEqual(certificate.RawData))
                    { ingressChain.Remove(chainCertificate); chainCertificate.Dispose(); }
                if (ingressChain.Cast<X509Certificate2>().Any(item => item.NotAfter.ToUniversalTime() <= DateTime.UtcNow || item.NotBefore.ToUniversalTime() > DateTime.UtcNow ||
                    item.Extensions.OfType<X509BasicConstraintsExtension>().FirstOrDefault() is not { CertificateAuthority: true }))
                    throw new InvalidOperationException("Invalid HTTP intermediate certificate chain.");
                if (!certificate.HasPrivateKey || certificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow || certificate.NotBefore.ToUniversalTime() > DateTime.UtcNow)
                    throw new InvalidOperationException("Invalid HTTP certificate.");
                var hostnames = channel.AuthorizedClientsOnly ? [options.PeerAuthorityHost] : options.PublicAllowedAuthorities
                    .Select(authority => new Uri(HttpProxyPolicy.NormalizeAuthority(authority, scheme)).Host).ToArray();
                if (hostnames.Any(hostname => !certificate.MatchesHostname(hostname, allowCommonName: false)))
                    throw new InvalidOperationException("HTTP certificate SAN does not match the configured entry.");
                // Windows TLS requires a persisted compatible key handle after PEM import.
                if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
                {
                    var compatible = X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), null,
                        OperatingSystem.IsWindows() ? X509KeyStorageFlags.UserKeySet : X509KeyStorageFlags.DefaultKeySet);
                    certificate.Dispose(); certificate = compatible;
                }
            }
            if (options.UpstreamTrustRef is { } trust)
            {
                roots = new X509Certificate2Collection();
                roots.ImportFromPemFile(Path.Combine(resources, trust + ".ca.pem"));
                if (roots.Count == 0 || roots.Cast<X509Certificate2>().Any(root => root.HasPrivateKey || root.NotAfter.ToUniversalTime() <= DateTime.UtcNow ||
                    root.NotBefore.ToUniversalTime() > DateTime.UtcNow || root.Extensions.OfType<X509BasicConstraintsExtension>().FirstOrDefault() is not { CertificateAuthority: true }))
                    throw new InvalidOperationException("Invalid upstream trust resource.");
            }
            var handler = new SocketsHttpHandler
            {
                UseProxy = false, UseCookies = false, AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None,
                MaxConnectionsPerServer = channel.MaxConnections, ConnectTimeout = TimeSpan.FromSeconds(channel.TargetConnectTimeoutSeconds),
                // HTTP/1.1 fresh connections avoid SocketsHttpHandler's stale pooled-connection replay path.
                PooledConnectionIdleTimeout = TimeSpan.FromSeconds(60), PooledConnectionLifetime = TimeSpan.Zero,
                ConnectCallback = async (_, cancellation) =>
                {
                    var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    try { await socket.ConnectAsync(channel.TargetHost, channel.TargetPort, cancellation); return new NetworkStream(socket, ownsSocket: true); }
                    catch { socket.Dispose(); throw; }
                }
            };
            if (roots is not null)
            {
                handler.SslOptions.CertificateChainPolicy = new X509ChainPolicy
                {
                    TrustMode = X509ChainTrustMode.CustomRootTrust, RevocationMode = X509RevocationMode.NoCheck
                };
                handler.SslOptions.CertificateChainPolicy.CustomTrustStore.AddRange(roots);
            }
            client = new HttpMessageInvoker(handler, disposeHandler: true);
            var transport = new VirtualHttpTransport(Math.Min(channel.MaxConnections, 32));
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ApplicationName = typeof(HttpProxyHost).Assembly.FullName });
            builder.Logging.ClearProviders(); // Never log request URLs, query strings, headers or bodies.
            builder.Services.AddHttpForwarder();
            builder.WebHost.ConfigureKestrel(kestrel =>
            {
                kestrel.Limits.MaxRequestBodySize = options.MaxRequestBodyBytes;
                kestrel.Limits.MaxRequestHeadersTotalSize = options.MaxRequestHeaderBytes;
                kestrel.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(options.HeaderTimeoutSeconds);
                kestrel.Limits.MaxConcurrentConnections = channel.MaxConnections;
                kestrel.Limits.MaxConcurrentUpgradedConnections = channel.MaxConnections;
                kestrel.Listen(new VirtualHttpEndpoint(), listen =>
                {
                    listen.Protocols = HttpProtocols.Http1;
                    if (certificate is not null) listen.UseHttps(certificate, https =>
                    {
                        https.ServerCertificateChain = ingressChain;
                        https.HandshakeTimeout = TimeSpan.FromSeconds(options.HeaderTimeoutSeconds);
                    });
                });
            });
            builder.Services.RemoveAll<IConnectionListenerFactory>();
            builder.Services.AddSingleton<IConnectionListenerFactory>(transport);
            app = builder.Build();
            HttpProxyPolicy.ResolveWebSocketConnectionCap(channel.MaxConnections, options.MaxWebSocketConnections);
            var host = new HttpProxyHost(channel, transport, app, client, certificate, roots, ingressChain);
            app.Run(async context =>
            {
                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                try { await host.ForwardAsync(context); }
                finally
                {
                    var feature = context.Features.Get<HttpIngressFeature>();
                    logger?.LogInformation("HTTP request on {ChannelId} connection {ConnectionId} via {AccessKind}, caller {CallerClientId}, mapping {MappingId} ended with {StatusCode} after {ElapsedMs} ms; aborted: {Aborted}.",
                        channel.ChannelId, feature?.ConnectionId, feature?.Context.AccessKind, feature?.Context.CallerClientId, feature?.Context.MappingId, context.Response.StatusCode,
                        System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds, context.RequestAborted.IsCancellationRequested);
                }
            });
            await app.StartAsync(token);
            return host;
        }
        catch
        {
            if (app is not null) await app.DisposeAsync();
            client?.Dispose(); certificate?.Dispose();
            if (ingressChain is not null) foreach (var item in ingressChain) item.Dispose();
            if (roots is not null) foreach (var root in roots) root.Dispose();
            throw;
        }
    }

    public void Revoke() { lifetime.Cancel(); transport.AbortAll(); }

    public async Task ServeAsync(Stream stream, HttpIngressContext ingress, CancellationToken token, Guid? connectionId = null)
    {
        if (IsDisposed || Revoked)
        {
            await stream.DisposeAsync();
            throw new IOException("HTTP ingress is unavailable.");
        }
        CancellationTokenSource linked;
        try { linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token); }
        catch (ObjectDisposedException)
        {
            await stream.DisposeAsync();
            throw new IOException("HTTP ingress is unavailable.");
        }
        using (linked)
        {
            var connection = new VirtualHttpConnection(stream, ingress, connectionId);
            try
            {
                if (!transport.TryEnqueue(connection)) { await connection.DisposeAsync(); throw new IOException("HTTP ingress queue is full."); }
                await connection.Completion.WaitAsync(linked.Token);
            }
            catch (ObjectDisposedException) { throw new IOException("HTTP ingress is unavailable."); }
            finally { connection.Abort(); await connection.DisposeAsync(); }
        }
    }

    private async Task ForwardAsync(HttpContext context)
    {
        var ingress = context.Features.Get<HttpIngressFeature>()?.Context;
        if (ingress is null || Revoked) { context.Response.StatusCode = 503; return; }
        if (context.Request.Method == "CONNECT") { context.Response.StatusCode = 405; return; }
        string originalAuthority;
        try { originalAuthority = HttpProxyPolicy.NormalizeAuthority(context.Request.Host.Value ?? string.Empty, ingress.Scheme); }
        catch (ArgumentException) { context.Response.StatusCode = 400; return; }
        var peer = ingress.AccessKind == "peer";
        var allowed = peer ? new[] { ingress.EntryAuthority ?? string.Empty } : options.PublicAllowedAuthorities;
        if (!allowed.Any(authority => SafeAuthority(authority, ingress.Scheme) == originalAuthority)) { context.Response.StatusCode = 421; return; }
        var isWebSocket = HttpProxyPolicy.IsWebSocketUpgrade(context.Request.Headers.Upgrade);
        var originValues = context.Request.Headers.Origin;
        if (originValues.Count > 1 || (isWebSocket && originValues.Count != 1)) { context.Response.StatusCode = 403; return; }
        if (originValues.Count == 1)
        {
            string origin;
            try { origin = HttpProxyPolicy.NormalizeOrigin(originValues[0]!); }
            catch (ArgumentException) { context.Response.StatusCode = 403; return; }
            var origins = peer ? options.PeerAllowedOrigins : options.PublicAllowedOrigins;
            if (!HttpProxyPolicy.OriginPermitted(peer, origins, origin, originalAuthority, isWebSocket))
            { context.Response.StatusCode = 403; return; }
        }
        var websocketSlot = false;
        var requestSlot = false;
        try
        {
            if (isWebSocket)
            {
                if (!await websockets.WaitAsync(0, context.RequestAborted)) { context.Response.StatusCode = 503; return; }
                websocketSlot = true;
            }
            if (!await requests.WaitAsync(0, context.RequestAborted)) { context.Response.StatusCode = 503; return; }
            requestSlot = true;
            var transformer = new HttpIngressTransformer(options, ingress, context.Request.Host.Value ?? string.Empty);
            var config = new ForwarderRequestConfig { Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                ActivityTimeout = HttpProxyPolicy.ActivityTimeout(peer, isWebSocket, options.ActivityTimeoutSeconds, options.WebSocketActivityTimeoutSeconds) };
            var forwarder = context.RequestServices.GetRequiredService<IHttpForwarder>();
            await forwarder.SendAsync(context, $"{options.UpstreamScheme}://{options.UpstreamAuthority}/", client, config, transformer);
        }
        catch (ObjectDisposedException)
        {
            if (!context.Response.HasStarted) context.Response.StatusCode = 503;
        }
        finally
        {
            if (requestSlot) try { requests.Release(); } catch (ObjectDisposedException) { }
            if (websocketSlot) try { websockets.Release(); } catch (ObjectDisposedException) { }
        }
    }

    private static string? SafeAuthority(string value, string scheme)
    {
        try { return HttpProxyPolicy.NormalizeAuthority(value, scheme); } catch (ArgumentException) { return null; }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposeStarted, 1) != 0) return;
        Revoke();
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await app.StopAsync(deadline.Token); }
            catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException) { }
            await app.DisposeAsync();
        }
        catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException) { }
        finally
        {
            try { client.Dispose(); } catch (ObjectDisposedException) { }
            try { certificate?.Dispose(); } catch (ObjectDisposedException) { }
            if (ingressChain is not null) foreach (var item in ingressChain) try { item.Dispose(); } catch (ObjectDisposedException) { }
            if (roots is not null) foreach (var root in roots) try { root.Dispose(); } catch (ObjectDisposedException) { }
            try { lifetime.Dispose(); } catch (ObjectDisposedException) { }
            try { requests.Dispose(); } catch (ObjectDisposedException) { }
            try { websockets.Dispose(); } catch (ObjectDisposedException) { }
            Volatile.Write(ref disposed, 1);
        }
    }
}

internal sealed class HttpIngressTransformer(HttpProxyOptions options, HttpIngressContext ingress, string originalHost) : HttpTransformer
{
    public override async ValueTask TransformRequestAsync(HttpContext context, HttpRequestMessage request, string destinationPrefix, CancellationToken token)
    {
        await base.TransformRequestAsync(context, request, destinationPrefix, token);
        RemoveUntrustedHeaders(request.Headers, options.StripRequestHeaders);
        if (request.Content is not null) RemoveUntrustedHeaders(request.Content.Headers, options.StripRequestHeaders);
        request.Headers.Host = options.HostMode switch { "preserve" => originalHost, "fixed" => options.HostValue, _ => options.UpstreamAuthority };
        if (options.OriginMode == "fixed" && context.Request.Headers.ContainsKey("Origin"))
        { request.Headers.Remove("Origin"); request.Headers.TryAddWithoutValidation("Origin", options.OriginValue); }
        request.Headers.TryAddWithoutValidation("X-Forwarded-Host", originalHost);
        request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", ingress.Scheme);
        if (ingress.AccessKind == "public" && IPAddress.TryParse(ingress.SourceIp, out var source))
            request.Headers.TryAddWithoutValidation("X-Forwarded-For", source.ToString());
    }

    private static void RemoveUntrustedHeaders(HttpHeaders headers, IReadOnlyList<string>? configured)
    {
        foreach (var name in headers.Select(header => header.Key).Where(name => IsUntrustedClientHeader(name, configured)).ToArray())
            headers.Remove(name);
    }

    private static bool IsUntrustedClientHeader(string name, IReadOnlyList<string>? configured) =>
        name.Equals("Forwarded", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase) ||
        HttpProxyPolicy.DefaultStripRequestHeaders.Any(candidate => name.Equals(candidate, StringComparison.OrdinalIgnoreCase)) ||
        configured is not null && configured.Any(candidate => name.Equals(candidate, StringComparison.OrdinalIgnoreCase));
}

internal sealed record HttpIngressFeature(HttpIngressContext Context, Guid? ConnectionId);
internal sealed class VirtualHttpEndpoint : EndPoint
{
    public override string ToString() => "relaylink-http";
}
internal sealed record StreamDuplexPipe(PipeReader Input, PipeWriter Output) : IDuplexPipe;

internal sealed class VirtualHttpConnection : ConnectionContext, IConnectionLifetimeFeature, IConnectionEndPointFeature
{
    private readonly Stream stream;
    private readonly CancellationTokenSource closed = new();
    private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int disposed;
    public Task Completion => completion.Task;
    public override string ConnectionId { get; set; } = Guid.NewGuid().ToString();
    public override IFeatureCollection Features { get; } = new FeatureCollection();
    public override IDictionary<object, object?> Items { get; set; } = new Dictionary<object, object?>();
    public override IDuplexPipe Transport { get; set; }
    public override CancellationToken ConnectionClosed { get; set; }
    public override EndPoint? LocalEndPoint { get; set; }
    public override EndPoint? RemoteEndPoint { get; set; }
    public VirtualHttpConnection(Stream stream, HttpIngressContext context, Guid? connectionId)
    {
        this.stream = stream;
        ConnectionClosed = closed.Token;
        Transport = new StreamDuplexPipe(PipeReader.Create(stream, new StreamPipeReaderOptions(leaveOpen: true)), PipeWriter.Create(stream, new StreamPipeWriterOptions(leaveOpen: true)));
        Features.Set<IConnectionLifetimeFeature>(this);
        Features.Set<IConnectionEndPointFeature>(this);
        Features.Set(new HttpIngressFeature(context, connectionId));
        LocalEndPoint = new VirtualHttpEndpoint();
        RemoteEndPoint = IPAddress.TryParse(context.SourceIp, out var ip) ? new IPEndPoint(ip, 0) : new IPEndPoint(IPAddress.Loopback, 0);
    }
    public override void Abort(ConnectionAbortedException reason)
    {
        if (Volatile.Read(ref disposed) != 0) return;
        try { closed.Cancel(); } catch (ObjectDisposedException) { }
        stream.Dispose();
    }
    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        try { closed.Cancel(); await Transport.Input.CompleteAsync(); await Transport.Output.CompleteAsync(); }
        finally { stream.Dispose(); closed.Dispose(); completion.TrySetResult(); }
    }
}

internal sealed class VirtualHttpTransport(int capacity) : IConnectionListenerFactory, IConnectionListener
{
    private readonly Channel<VirtualHttpConnection> queue = Channel.CreateBounded<VirtualHttpConnection>(new BoundedChannelOptions(capacity) { FullMode = BoundedChannelFullMode.Wait });
    private readonly ConcurrentDictionary<string, VirtualHttpConnection> active = new();
    public EndPoint EndPoint { get; } = new VirtualHttpEndpoint();
    public bool TryEnqueue(VirtualHttpConnection connection) => queue.Writer.TryWrite(connection);
    public ValueTask<IConnectionListener> BindAsync(EndPoint endpoint, CancellationToken cancellationToken = default) => ValueTask.FromResult<IConnectionListener>(this);
    public async ValueTask<ConnectionContext?> AcceptAsync(CancellationToken token = default)
    {
        if (!await queue.Reader.WaitToReadAsync(token)) return null;
        if (!queue.Reader.TryRead(out var connection)) return await AcceptAsync(token);
        active[connection.ConnectionId] = connection;
        _ = connection.Completion.ContinueWith(_ => active.TryRemove(connection.ConnectionId, out var ignored), TaskScheduler.Default);
        return connection;
    }
    public void AbortAll()
    {
        queue.Writer.TryComplete();
        while (queue.Reader.TryRead(out var pending)) { pending.Abort(); _ = pending.DisposeAsync(); }
        foreach (var connection in active.Values) connection.Abort();
    }
    public ValueTask UnbindAsync(CancellationToken token = default) { AbortAll(); return ValueTask.CompletedTask; }
    public ValueTask DisposeAsync() { AbortAll(); return ValueTask.CompletedTask; }
}
