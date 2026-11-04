using System.Diagnostics;
using System.IO.Pipelines;
using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RelayLink.Server.Configuration;
using Yarp.ReverseProxy.Forwarder;

namespace RelayLink.Server.Runtime;

public sealed record HttpInspectionDecision(long RequestId, bool Allowed, string Reason, string Summary);
public sealed record HttpInspectionResponse(long RequestId, int StatusCode, string Summary, long BytesToTarget, long BytesToCaller, double ElapsedMilliseconds,
    string Outcome, string Reason);
public sealed record HttpInspectionRelayOptions
{
    public TimeSpan SetupTimeout { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan BlockedWriteTimeout { get; init; } = TimeSpan.FromSeconds(120);
    public TimeSpan HalfCloseTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public Action? Abort { get; init; }
    public Func<CancellationToken, ValueTask<IDisposable?>>? AcquireApprovalLease { get; init; }
    public Func<HttpInspectionDecision, CancellationToken, ValueTask>? OnApprovalWaiting { get; init; }
}

public static partial class HttpInspection
{
    /// <summary>
    /// Runs a HTTP/1.1 proxy on an already authenticated relay stream. Kestrel owns message
    /// framing; every request passes policy and durable audit before YARP receives it.
    /// The target callback is consumed at most once: a closed target cannot trigger replay.
    /// The caller retains ownership of both relay streams.
    /// </summary>
    public static async Task<bool> RelayAsync(Stream caller, ChannelConfiguration channel,
        Func<CancellationToken, Task<Stream>> openTarget,
        Func<HttpInspectionDecision, CancellationToken, ValueTask> onDecision,
        Func<HttpInspectionResponse, CancellationToken, ValueTask>? onResponse,
        CancellationToken cancellationToken, Action<int>? fromCaller = null, Action<int>? fromTarget = null,
        Func<CancellationToken, ValueTask>? onEstablished = null, HttpInspectionRelayOptions? options = null)
    {
        options ??= new HttpInspectionRelayOptions();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var inputDeadline = new CancellationTokenSource();
        using var inputDeadlineRegistration = inputDeadline.Token.Register(() => lifetime.Cancel());
        var draining = 0;
        void BeginDrain()
        {
            if (Interlocked.Exchange(ref draining, 1) == 0) lifetime.CancelAfter(options.HalfCloseTimeout);
        }
        Stream application = new InspectionStream(caller, lifetime.Token, options.BlockedWriteTimeout, eof: BeginDrain);
        X509Certificate2? certificate = null;
        WebApplication? app = null;
        InspectionConnection? connection = null;
        HttpMessageInvoker? client = null;
        var established = false;
        var targetConsumed = 0;
        long requestId = 0, toTarget = 0, toCaller = 0;
        Exception? failure = null;
        try
        {
            if (channel.Application == "https")
            {
                var material = channel.ResolvedInspection!.Https!;
                certificate = ServerCertificateLoader.Load(material.CertificatePemPath, material.PrivateKeyPemPath);
                var tls = new SslStream(application, leaveInnerStreamOpen: false);
                application = tls;
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                deadline.CancelAfter(options.SetupTimeout);
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    ApplicationProtocols = [SslApplicationProtocol.Http11],
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck
                }, deadline.Token);
                // HTTP/1.1 also permits clients without ALPN (e.g. older command-line clients).
                if (!tls.IsEncrypted || tls.NegotiatedApplicationProtocol != default && tls.NegotiatedApplicationProtocol != SslApplicationProtocol.Http11)
                    throw new AuthenticationException("Caller TLS did not negotiate HTTP/1.1.");
            }

            var handler = new SocketsHttpHandler
            {
                UseProxy = false, UseCookies = false, AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.None,
                MaxConnectionsPerServer = 1,
                ConnectTimeout = Timeout.InfiniteTimeSpan,
                PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan,
                PooledConnectionLifetime = Timeout.InfiniteTimeSpan,
                MaxResponseHeadersLength = MaxHeaderBytes / 1024,
                ConnectCallback = async (_, token) =>
                {
                    if (Interlocked.Exchange(ref targetConsumed, 1) != 0)
                        throw new IOException("Inspection target stream cannot be reopened or replayed.");
                    var pipe = await openTarget(token);
                    Stream upstream = new InspectionStream(pipe, lifetime.Token, options.BlockedWriteTimeout);
                    try
                    {
                        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
                        deadline.CancelAfter(options.SetupTimeout);
                        upstream = await OpenUpstreamAsync(upstream, channel, deadline.Token);
                        if (onEstablished is not null) await onEstablished(deadline.Token);
                        established = true;
                        return new InspectionStream(upstream, lifetime.Token, options.BlockedWriteTimeout,
                            read: count => { Interlocked.Add(ref toCaller, count); fromTarget?.Invoke(count); },
                            written: count => { Interlocked.Add(ref toTarget, count); fromCaller?.Invoke(count); }, ownsInner: true, eof: BeginDrain);
                    }
                    catch { await upstream.DisposeAsync(); throw; }
                }
            };
            client = new HttpMessageInvoker(handler, disposeHandler: true);
            var transport = new InspectionTransport();
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
            {
                Args = [], ApplicationName = typeof(HttpInspection).Assembly.FullName,
                // No ambient appsettings or logging configuration may enable URL/body logging.
                ContentRootPath = AppContext.BaseDirectory
            });
            builder.Logging.ClearProviders();
            builder.Services.AddHttpForwarder();
            builder.WebHost.ConfigureKestrel(kestrel =>
            {
                kestrel.AddServerHeader = false;
                kestrel.Limits.MaxRequestHeadersTotalSize = MaxHeaderBytes;
                kestrel.Limits.MaxRequestHeaderCount = 100;
                kestrel.Limits.MaxRequestLineSize = 8 * 1024;
                kestrel.Limits.MaxRequestBodySize = null; // Streaming, never buffered in full.
                kestrel.Limits.MaxRequestBufferSize = MaxHeaderBytes;
                kestrel.Limits.MaxResponseBufferSize = 32 * 1024;
                kestrel.Limits.RequestHeadersTimeout = options.SetupTimeout;
                kestrel.Limits.KeepAliveTimeout = TimeSpan.MaxValue; // Relay lifetime/idle owns this timeout.
                kestrel.Limits.MinRequestBodyDataRate = null;
                kestrel.Limits.MinResponseDataRate = null;
                kestrel.Limits.MaxConcurrentConnections = 1;
                kestrel.Limits.MaxConcurrentUpgradedConnections = 1;
                kestrel.Listen(new InspectionEndpoint(), listen => listen.Protocols = HttpProtocols.Http1);
            });
            builder.Services.RemoveAll<IConnectionListenerFactory>();
            builder.Services.AddSingleton<IConnectionListenerFactory>(transport);
            app = builder.Build();
            var forwarder = app.Services.GetRequiredService<IHttpForwarder>();
            var forwardOptions = new ForwarderRequestConfig
            {
                Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                ActivityTimeout = Timeout.InfiniteTimeSpan
            };
            app.Run(async context =>
            {
                var id = Interlocked.Increment(ref requestId);
                if (id == 1) inputDeadline.CancelAfter(Timeout.InfiniteTimeSpan);
                var started = Stopwatch.GetTimestamp();
                var requestStart = Interlocked.Read(ref toTarget);
                var responseStart = Interlocked.Read(ref toCaller);
                var summary = Summary(context.Request.Method, context.Request.Host.Host, context.Request.Path.Value ?? "/");
                var outcome = "failed";
                var resultReason = "inspection_failed";
                using var requestLifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, lifetime.Token);
                try
                {
                    var invalid = InvalidRequest(context);
                    var (action, reason) = invalid is null
                        ? await DecideAsync(channel.Inspection, context.Request.Method, context.Request.Host.Host, context.Request.Path.Value ?? "/", requestLifetime.Token,
                            options.AcquireApprovalLease, options.OnApprovalWaiting is null ? null :
                                token => options.OnApprovalWaiting(new HttpInspectionDecision(id, false, "approval_waiting", summary), token))
                        : ("deny", invalid);
                    resultReason = "decision_audit_failed";
                    await onDecision(new HttpInspectionDecision(id, action == "allow", reason, summary), requestLifetime.Token);
                    if (action != "allow")
                    {
                        outcome = "denied";
                        resultReason = reason;
                        context.Response.StatusCode = invalid is null ? StatusCodes.Status403Forbidden : StatusCodes.Status400BadRequest;
                        context.Response.ContentLength = 0;
                        context.Response.Headers.Connection = "close";
                        return;
                    }

                    var transformer = new InspectionTransformer();
                    resultReason = "forwarding_failed";
                    var result = await forwarder.SendAsync(context, "http://relaylink.inspection.invalid/", client, forwardOptions, transformer, requestLifetime.Token);
                    // A single relay owns exactly one upstream connection. Do not invite a
                    // subsequent request when the upstream has closed it or forwarding failed.
                    if (!context.Response.HasStarted && (transformer.UpstreamClosed || result != ForwarderError.None))
                        context.Response.Headers.Connection = "close";
                    if (result != ForwarderError.None)
                    {
                        resultReason = "forwarder_" + result;
                        failure ??= new IOException("Inspected HTTP forwarding failed: " + result);
                        context.Abort();
                    }
                    else
                    {
                        outcome = "success";
                        resultReason = "completed";
                    }
                }
                catch (Exception exception)
                {
                    if (exception is OperationCanceledException)
                    {
                        outcome = "cancelled";
                        resultReason = "cancelled";
                    }
                    failure ??= exception;
                    context.Abort();
                    lifetime.Cancel();
                    options.Abort?.Invoke();
                }
                finally
                {
                    if (onResponse is not null)
                    {
                        try
                        {
                            // Business abort/revocation may already have cancelled the relay.
                            // Preserve its result with a separate, bounded cleanup deadline.
                            using var auditDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                            var status = outcome is "success" or "denied" || context.Response.HasStarted ? context.Response.StatusCode : 0;
                            await onResponse(new HttpInspectionResponse(id, status, summary,
                                Interlocked.Read(ref toTarget) - requestStart, Interlocked.Read(ref toCaller) - responseStart,
                                Stopwatch.GetElapsedTime(started).TotalMilliseconds, outcome, resultReason), auditDeadline.Token);
                        }
                        catch (Exception exception)
                        {
                            // Propagate to the relay owner for its diagnostic/close record;
                            // the already forwarded operation is never retried or rolled back.
                            failure ??= exception;
                            context.Abort();
                        }
                    }
                }
            });
            await app.StartAsync(lifetime.Token);
            inputDeadline.CancelAfter(options.SetupTimeout);
            connection = new InspectionConnection(application, lifetime.Token, options.Abort);
            transport.Enqueue(connection);
            try { await connection.Completion.WaitAsync(lifetime.Token); }
            catch (OperationCanceledException) when (failure is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
                throw;
            }
            if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            return established;
        }
        finally
        {
            lifetime.Cancel();
            if (connection is not null) await connection.DisposeAsync();
            client?.Dispose();
            if (app is not null)
            {
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await app.StopAsync(stop.Token); } catch (OperationCanceledException) { }
                await app.DisposeAsync();
            }
            await application.DisposeAsync();
            certificate?.Dispose();
        }
    }

    private static string? InvalidRequest(HttpContext context)
    {
        var request = context.Request;
        if (request.Protocol != "HTTP/1.1" || request.Method.Length is < 1 or > 16) return "malformed_http";
        // Kestrel preserves a removed conflicting Content-Length under X-Content-Length.
        // Reject that marker rather than forwarding an ambiguous CL+TE request.
        if (request.Headers.ContainsKey("X-Content-Length")) return "ambiguous_framing";
        if (request.Headers.TransferEncoding.Count > 0 &&
            !string.Equals(request.Headers.TransferEncoding.ToString(), "chunked", StringComparison.OrdinalIgnoreCase)) return "unsupported_transfer_encoding";
        if (request.Method == "CONNECT") return "unsupported_connect";
        if (request.Headers.Upgrade.Count > 0 &&
            (!string.Equals(request.Headers.Upgrade.ToString(), "websocket", StringComparison.OrdinalIgnoreCase) ||
             request.Method != "GET" || request.ContentLength is > 0 || request.Headers.TransferEncoding.Count > 0 ||
             context.Features.Get<IHttpUpgradeFeature>()?.IsUpgradableRequest != true)) return "unsupported_upgrade";
        // Only origin-form targets are supported. This prevents an absolute-form authority
        // or encoded path from being authorized differently from the forwarded URI.
        var rawTarget = context.Features.Get<IHttpRequestFeature>()?.RawTarget ?? "";
        if (!rawTarget.StartsWith('/') || rawTarget.Contains('#')) return "unsupported_request_target";
        return null;
    }

    private sealed class InspectionTransformer : HttpTransformer
    {
        public bool UpstreamClosed { get; private set; }
        public override async ValueTask TransformRequestAsync(HttpContext context, HttpRequestMessage request, string destinationPrefix, CancellationToken cancellationToken)
        {
            await base.TransformRequestAsync(context, request, destinationPrefix, cancellationToken);
            request.Headers.Host = context.Request.Host.Value;
        }
        public override async ValueTask<bool> TransformResponseAsync(HttpContext context, HttpResponseMessage? response, CancellationToken cancellationToken)
        {
            var result = await base.TransformResponseAsync(context, response, cancellationToken);
            UpstreamClosed = response?.Headers.ConnectionClose == true;
            if (UpstreamClosed) context.Response.Headers.Connection = "close";
            return result;
        }
    }
}

internal sealed class InspectionEndpoint : EndPoint
{
    public override string ToString() => "relaylink-inspection";
}
internal sealed record InspectionPipe(PipeReader Input, PipeWriter Output) : IDuplexPipe;

internal sealed class InspectionConnection : ConnectionContext, IConnectionLifetimeFeature
{
    private readonly CancellationTokenSource closed;
    private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Action? abort;
    private int disposed;
    public Task Completion => completion.Task;
    public override string ConnectionId { get; set; } = Guid.NewGuid().ToString();
    public override IFeatureCollection Features { get; } = new FeatureCollection();
    public override IDictionary<object, object?> Items { get; set; } = new Dictionary<object, object?>();
    public override IDuplexPipe Transport { get; set; }
    public override CancellationToken ConnectionClosed { get; set; }
    public InspectionConnection(Stream stream, CancellationToken token, Action? abort)
    {
        this.abort = abort;
        closed = CancellationTokenSource.CreateLinkedTokenSource(token);
        ConnectionClosed = closed.Token;
        Transport = new InspectionPipe(PipeReader.Create(stream, new StreamPipeReaderOptions(bufferSize: 16 * 1024, leaveOpen: true)),
            PipeWriter.Create(stream, new StreamPipeWriterOptions(leaveOpen: true)));
        Features.Set<IConnectionLifetimeFeature>(this);
    }
    public override void Abort(ConnectionAbortedException abortReason)
    {
        if (Volatile.Read(ref disposed) != 0) return;
        try { closed.Cancel(); } catch (ObjectDisposedException) { }
        Transport.Input.CancelPendingRead();
        Transport.Output.CancelPendingFlush();
        abort?.Invoke();
    }
    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        try
        {
            closed.Cancel();
            await Transport.Input.CompleteAsync();
            await Transport.Output.CompleteAsync();
        }
        finally { closed.Dispose(); completion.TrySetResult(); }
    }
}

internal sealed class InspectionTransport : IConnectionListenerFactory, IConnectionListener
{
    private readonly Channel<InspectionConnection> queue = Channel.CreateBounded<InspectionConnection>(1);
    public EndPoint EndPoint { get; } = new InspectionEndpoint();
    public void Enqueue(InspectionConnection connection)
    {
        if (!queue.Writer.TryWrite(connection)) throw new IOException("Inspection connection queue is unavailable.");
    }
    public ValueTask<IConnectionListener> BindAsync(EndPoint endpoint, CancellationToken cancellationToken = default) => ValueTask.FromResult<IConnectionListener>(this);
    public async ValueTask<ConnectionContext?> AcceptAsync(CancellationToken cancellationToken = default) =>
        await queue.Reader.WaitToReadAsync(cancellationToken) && queue.Reader.TryRead(out var connection) ? connection : null;
    public ValueTask UnbindAsync(CancellationToken cancellationToken = default) { queue.Writer.TryComplete(); return ValueTask.CompletedTask; }
    public ValueTask DisposeAsync() { queue.Writer.TryComplete(); return ValueTask.CompletedTask; }
}

internal sealed class InspectionStream(Stream inner, CancellationToken lifetime, TimeSpan writeTimeout,
    Action<int>? read = null, Action<int>? written = null, bool ownsInner = false, Action? eof = null) : Stream
{
    public override bool CanRead => inner.CanRead;
    public override bool CanWrite => inner.CanWrite;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => inner.Flush();
    public override async Task FlushAsync(CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime);
        deadline.CancelAfter(writeTimeout);
        await inner.FlushAsync(deadline.Token);
    }
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime);
        var count = await inner.ReadAsync(buffer, linked.Token);
        if (count > 0) read?.Invoke(count);
        else if (!buffer.IsEmpty) eof?.Invoke();
        return count;
    }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) => ReadAsync(buffer.AsMemory(offset, count), token).AsTask();
    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime);
        deadline.CancelAfter(writeTimeout);
        await inner.WriteAsync(buffer, deadline.Token);
        if (!buffer.IsEmpty) written?.Invoke(buffer.Length);
    }
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token) => WriteAsync(buffer.AsMemory(offset, count), token).AsTask();
    protected override void Dispose(bool disposing) { if (disposing && ownsInner) inner.Dispose(); base.Dispose(disposing); }
    public override async ValueTask DisposeAsync() { if (ownsInner) await inner.DisposeAsync(); GC.SuppressFinalize(this); }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
