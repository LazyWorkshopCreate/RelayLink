using System.Net;
using System.Net.Sockets;
using System.Text;
using RelayLink.Server.Configuration;
using RelayLink.Server.Runtime;

namespace RelayLink.UnitTests;

public sealed class HttpInspectionOutcomeTests
{
    [Fact]
    public async Task Truncated_200_is_a_failed_request_even_after_response_headers_started()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var caller = await Pair.CreateAsync(timeout.Token);
        using var target = await Pair.CreateAsync(timeout.Token);
        var response = new TaskCompletionSource<HttpInspectionResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var opens = 0;
        var serving = Task.Run(async () =>
        {
            await Header(target.Client, timeout.Token);
            await Send(target.Client, "HTTP/1.1 200 OK\r\nContent-Length: 10\r\n\r\nshort", timeout.Token);
            target.Client.Dispose();
        });
        var relay = HttpInspection.RelayAsync(caller.Server, Channel(),
            _ => { opens++; return Task.FromResult<Stream>(target.Server); }, (_, _) => ValueTask.CompletedTask,
            (result, token) => { token.ThrowIfCancellationRequested(); response.TrySetResult(result); return ValueTask.CompletedTask; },
            timeout.Token, options: Options(caller, target));
        await Send(caller.Client, "GET /partial?secret=hidden HTTP/1.1\r\nHost: example.test\r\n\r\n", timeout.Token);
        var result = await response.Task.WaitAsync(timeout.Token);
        await Assert.ThrowsAnyAsync<Exception>(async () => await relay.WaitAsync(timeout.Token));
        await serving;
        Assert.False(timeout.IsCancellationRequested);
        Assert.Equal("failed", result.Outcome);
        Assert.Equal(200, result.StatusCode);
        Assert.StartsWith("forwarder_", result.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("hidden", result.Summary, StringComparison.Ordinal);
        Assert.Equal(1, result.RequestId);
        Assert.True(result.BytesToTarget > 0);
        Assert.True(result.BytesToCaller > 0);
        Assert.Equal(1, opens);
    }

    [Fact]
    public async Task Target_open_failure_is_not_reported_as_default_200_or_success()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var caller = await Pair.CreateAsync(timeout.Token);
        var response = new TaskCompletionSource<HttpInspectionResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var opens = 0;
        var relay = HttpInspection.RelayAsync(caller.Server, Channel(),
            _ => { opens++; throw new IOException("Target unavailable."); }, (_, _) => ValueTask.CompletedTask,
            (result, token) => { token.ThrowIfCancellationRequested(); response.TrySetResult(result); return ValueTask.CompletedTask; },
            timeout.Token, options: Options(caller));
        await Send(caller.Client, "GET /failed HTTP/1.1\r\nHost: example.test\r\n\r\n", timeout.Token);
        var result = await response.Task.WaitAsync(timeout.Token);
        await Assert.ThrowsAnyAsync<Exception>(async () => await relay.WaitAsync(timeout.Token));
        Assert.False(timeout.IsCancellationRequested);
        Assert.Equal("failed", result.Outcome);
        Assert.Equal(0, result.StatusCode);
        Assert.StartsWith("forwarder_", result.Reason, StringComparison.Ordinal);
        Assert.Equal(0, result.BytesToTarget);
        Assert.Equal(0, result.BytesToCaller);
        Assert.Equal(1, opens);
    }

    [Fact]
    public async Task Failed_decision_audit_still_records_result_after_business_abort_cancels_owner()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var business = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        using var caller = await Pair.CreateAsync(timeout.Token);
        var response = new TaskCompletionSource<HttpInspectionResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var opens = 0;
        var relay = HttpInspection.RelayAsync(caller.Server, Channel(),
            _ => { opens++; throw new InvalidOperationException("Target must remain closed."); },
            (_, _) => throw new AuditUnavailableException("Decision sink unavailable."),
            async (result, token) =>
            {
                Assert.True(business.IsCancellationRequested);
                Assert.True(token.CanBeCanceled);
                await Task.Delay(20, token);
                response.TrySetResult(result);
            }, business.Token, options: Options(caller) with
            {
                Abort = () => { business.Cancel(); caller.Server.Dispose(); }
            });
        await Send(caller.Client, "GET /audit-failed HTTP/1.1\r\nHost: example.test\r\n\r\n", timeout.Token);
        var result = await response.Task.WaitAsync(timeout.Token);
        await Assert.ThrowsAnyAsync<Exception>(async () => await relay.WaitAsync(timeout.Token));
        Assert.False(timeout.IsCancellationRequested);
        Assert.Equal("failed", result.Outcome);
        Assert.Equal("decision_audit_failed", result.Reason);
        Assert.Equal(0, result.StatusCode);
        Assert.Equal(0, opens);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Completed_response_and_policy_denial_have_distinct_results(bool denied)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var caller = await Pair.CreateAsync(timeout.Token);
        using var target = await Pair.CreateAsync(timeout.Token);
        var response = new TaskCompletionSource<HttpInspectionResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var opens = 0;
        var serving = denied ? Task.CompletedTask : Task.Run(async () =>
        {
            await Header(target.Client, timeout.Token);
            await Send(target.Client, "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n", timeout.Token);
        });
        var relay = HttpInspection.RelayAsync(caller.Server, Channel(denied),
            _ => { opens++; return Task.FromResult<Stream>(target.Server); }, (_, _) => ValueTask.CompletedTask,
            (result, _) => { response.TrySetResult(result); return ValueTask.CompletedTask; },
            timeout.Token, options: Options(caller, target));
        await Send(caller.Client, "GET /result HTTP/1.1\r\nHost: example.test\r\n\r\n", timeout.Token);
        Assert.Contains(denied ? "403" : "200", await Header(caller.Client, timeout.Token), StringComparison.Ordinal);
        var result = await response.Task.WaitAsync(timeout.Token);
        await relay.WaitAsync(timeout.Token);
        await serving;
        Assert.Equal(denied ? "denied" : "success", result.Outcome);
        Assert.Equal(denied ? "rule_deny" : "completed", result.Reason);
        Assert.Equal(denied ? 403 : 200, result.StatusCode);
        Assert.Equal(denied ? 0 : 1, opens);
    }

    private static ChannelConfiguration Channel(bool denied = false) =>
        new("jump", "Jump", true, "127.0.0.1", 1, "127.0.0.1", 9, 2, 5)
        {
            InspectionMode = "server-jump", Tags = ["app:http"],
            Inspection = denied ? new InspectionPolicy { Rules = [new InspectionRule { PathPrefix = "/", Action = "deny" }] } : null
        };

    private static HttpInspectionRelayOptions Options(Pair caller, Pair? target = null) => new()
    {
        SetupTimeout = TimeSpan.FromSeconds(3), BlockedWriteTimeout = TimeSpan.FromSeconds(3),
        Abort = () => { caller.Server.Dispose(); target?.Server.Dispose(); }
    };

    private static Task Send(Stream stream, string text, CancellationToken token) => stream.WriteAsync(Encoding.ASCII.GetBytes(text), token).AsTask();

    private static async Task<string> Header(Stream stream, CancellationToken token)
    {
        var bytes = new List<byte>(); var next = new byte[1];
        while (bytes.Count < 65536)
        {
            if (await stream.ReadAsync(next, token) == 0) throw new EndOfStreamException();
            bytes.Add(next[0]);
            if (bytes.Count >= 4 && bytes[^4] == 13 && bytes[^3] == 10 && bytes[^2] == 13 && bytes[^1] == 10)
                return Encoding.ASCII.GetString(bytes.ToArray());
        }
        throw new InvalidDataException();
    }

    private sealed class Pair(TcpClient client, TcpClient server) : IDisposable
    {
        public NetworkStream Client { get; } = client.GetStream();
        public NetworkStream Server { get; } = server.GetStream();
        public static async Task<Pair> CreateAsync(CancellationToken token)
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var client = new TcpClient();
            var connecting = client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, token);
            var server = await listener.AcceptTcpClientAsync(token); await connecting;
            return new Pair(client, server);
        }
        public void Dispose() { client.Dispose(); server.Dispose(); }
    }
}
