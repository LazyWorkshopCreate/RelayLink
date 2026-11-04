using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using RelayLink.Server.Configuration;
using RelayLink.Server.Runtime;

namespace RelayLink.UnitTests;

public sealed class HttpInspectionRelayTests
{
    [Theory]
    [InlineData(true, "deny")]
    [InlineData(false, "deny")]
    [InlineData(true, "approve")]
    [InlineData(false, "approve")]
    public async Task Http_policy_denies_second_request_before_it_reaches_target(bool pipeline, string action)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var caller = await Pair.CreateAsync(timeout.Token);
        using var target = await Pair.CreateAsync(timeout.Token);
        var targetRequests = new List<string>();
        var decisions = new List<HttpInspectionDecision>();
        var responses = new List<HttpInspectionResponse>();
        var openCount = 0;
        var channel = Channel(new InspectionPolicy { ApprovalTimeoutSeconds = 1, Rules = [new InspectionRule { PathPrefix = "/secret", Action = action }] });
        var serving = Task.Run(async () =>
        {
            targetRequests.Add(await Header(target.Client, timeout.Token));
            await Send(target.Client, "HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\nok", timeout.Token);
            var buffer = new byte[1024];
            try
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                wait.CancelAfter(TimeSpan.FromSeconds(3));
                var read = await target.Client.ReadAsync(buffer, wait.Token);
                if (read > 0) targetRequests.Add(Encoding.ASCII.GetString(buffer, 0, read));
            }
            catch (OperationCanceledException) { }
        });
        var relay = HttpInspection.RelayAsync(caller.Server, channel, _ => { openCount++; return Task.FromResult<Stream>(target.Server); },
            (decision, _) => { decisions.Add(decision); return ValueTask.CompletedTask; },
            (response, _) => { responses.Add(response); return ValueTask.CompletedTask; }, timeout.Token,
            options: Options(caller, target));
        var first = "GET /health?token=hidden HTTP/1.1\r\nHost: example.test\r\nAuthorization: Bearer private-token\r\nCookie: private-cookie\r\n\r\n";
        var second = "POST /secret HTTP/1.1\r\nHost: example.test\r\nContent-Length: 0\r\n\r\n";
        await Send(caller.Client, first + (pipeline ? second : ""), timeout.Token);
        Assert.Contains("200", await Header(caller.Client, timeout.Token), StringComparison.Ordinal);
        var body = new byte[2];
        await caller.Client.ReadExactlyAsync(body, timeout.Token);
        Assert.Equal("ok", Encoding.ASCII.GetString(body));
        if (!pipeline) await Send(caller.Client, second, timeout.Token);
        Assert.Contains("403", await Header(caller.Client, timeout.Token), StringComparison.Ordinal);
        Assert.True(await relay.WaitAsync(timeout.Token));
        target.Server.Dispose();
        await serving;
        Assert.Equal(1, openCount);
        Assert.Single(targetRequests);
        Assert.DoesNotContain("POST /secret", targetRequests[0], StringComparison.Ordinal);
        Assert.Equal(2, decisions.Count);
        Assert.True(decisions[0].Allowed);
        Assert.False(decisions[1].Allowed);
        Assert.Equal(action == "deny" ? "rule_deny" : "approval_timeout", decisions[1].Reason);
        Assert.DoesNotContain("hidden", decisions[0].Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("private-", decisions[0].Summary, StringComparison.Ordinal);
        Assert.Equal(new[] { 200, 403 }, responses.Select(response => response.StatusCode));
    }

    [Fact]
    public async Task Http_counts_the_first_header_bytes_written_to_target()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var caller = await Pair.CreateAsync(timeout.Token);
        using var target = await Pair.CreateAsync(timeout.Token);
        var counted = 0;
        var serving = Task.Run(async () =>
        {
            var request = await Header(target.Client, timeout.Token);
            await Send(target.Client, "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n", timeout.Token);
            return Encoding.ASCII.GetByteCount(request);
        });
        var relay = HttpInspection.RelayAsync(caller.Server, Channel(), _ => Task.FromResult<Stream>(target.Server),
            (_, _) => ValueTask.CompletedTask, null, timeout.Token, fromCaller: count => counted += count, options: Options(caller, target));
        await Send(caller.Client, "GET /health HTTP/1.1\r\nHost: example.test\r\n\r\n", timeout.Token);
        Assert.Contains("200", await Header(caller.Client, timeout.Token), StringComparison.Ordinal);
        await relay.WaitAsync(timeout.Token);
        Assert.Equal(await serving, counted);
        Assert.True(counted > 0);
    }

    [Theory]
    [InlineData("GET /denied HTTP/1.1\r\nHost: example.test\r\n\r\n", "403")]
    [InlineData("POST /ok HTTP/1.1\r\nHost: example.test\r\nContent-Length: 0\r\nTransfer-Encoding: chunked\r\n\r\n0\r\n\r\n", "400")]
    [InlineData("GET /ok HTTP/1.1\r\nHost: example.test\r\nHost: other.test\r\n\r\n", "400")]
    [InlineData("POST /ok HTTP/1.1\r\nHost: example.test\r\nContent-Length: 1\r\nContent-Length: 1\r\n\r\nx", "400")]
    [InlineData("POST /ok HTTP/1.1\r\nHost: example.test\r\nTransfer-Encoding: gzip, chunked\r\n\r\n0\r\n\r\n", "400")]
    [InlineData("CONNECT example.test:443 HTTP/1.1\r\nHost: example.test\r\n\r\n", "400")]
    [InlineData("GET http://example.test/ok HTTP/1.1\r\nHost: example.test\r\n\r\n", "400")]
    [InlineData("GET /ok HTTP/1.0\r\nHost: example.test\r\n\r\n", "400")]
    [InlineData("GET /ok HTTP/1.1\r\nHost: example.test\r\nConnection: upgrade\r\nUpgrade: arbitrary\r\n\r\n", "400")]
    public async Task Rejected_first_request_never_opens_target(string request, string status)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var caller = await Pair.CreateAsync(timeout.Token);
        var opened = 0;
        var relay = HttpInspection.RelayAsync(caller.Server, Channel(new InspectionPolicy { Rules = [new InspectionRule { PathPrefix = "/denied", Action = "deny" }] }),
            _ => { opened++; throw new InvalidOperationException("Target must remain closed."); }, (_, _) => ValueTask.CompletedTask, null,
            timeout.Token, options: Options(caller));
        await Send(caller.Client, request, timeout.Token);
        Assert.Contains(status, await Header(caller.Client, timeout.Token), StringComparison.Ordinal);
        Assert.False(await relay.WaitAsync(timeout.Token));
        Assert.Equal(0, opened);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Bodies_stream_with_expect_continue_and_interim_response_before_final_status(bool chunked)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var caller = await Pair.CreateAsync(timeout.Token);
        using var target = await Pair.CreateAsync(timeout.Token);
        var statuses = new List<int>();
        var serving = Task.Run(async () =>
        {
            var header = await Header(target.Client, timeout.Token);
            await Send(target.Client, "HTTP/1.1 100 Continue\r\n\r\n", timeout.Token);
            var content = await Body(target.Client, header, timeout.Token);
            await Send(target.Client, "HTTP/1.1 103 Early Hints\r\nLink: </style.css>; rel=preload\r\n\r\nHTTP/1.1 201 Created\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok", timeout.Token);
            return content;
        });
        var relay = HttpInspection.RelayAsync(caller.Server, Channel(), _ => Task.FromResult<Stream>(target.Server), (_, _) => ValueTask.CompletedTask,
            (response, _) => { statuses.Add(response.StatusCode); return ValueTask.CompletedTask; }, timeout.Token, options: Options(caller, target));
        await Send(caller.Client, "POST /body HTTP/1.1\r\nHost: example.test\r\nExpect: 100-continue\r\n" + (chunked ? "Transfer-Encoding: chunked" : "Content-Length: 11") + "\r\n\r\n", timeout.Token);
        Assert.Contains("100", await Header(caller.Client, timeout.Token), StringComparison.Ordinal);
        foreach (var fragment in chunked ? new[] { "5\r\nhe", "llo\r\n", "6\r\n world\r\n0\r\n\r\n" } : new[] { "hello", " world" })
            await Send(caller.Client, fragment, timeout.Token);
        var response = await Header(caller.Client, timeout.Token);
        while (response.StartsWith("HTTP/1.1 1", StringComparison.Ordinal)) response = await Header(caller.Client, timeout.Token);
        Assert.Contains("201", response, StringComparison.Ordinal);
        var body = new byte[2];
        await caller.Client.ReadExactlyAsync(body, timeout.Token);
        await relay.WaitAsync(timeout.Token);
        Assert.Equal("hello world", await serving);
        Assert.Equal(new[] { 201 }, statuses);
    }

    [Fact]
    public async Task Websocket_upgrades_only_after_decision_then_relays_duplex_bytes()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var caller = await Pair.CreateAsync(timeout.Token);
        using var target = await Pair.CreateAsync(timeout.Token);
        var decisions = 0;
        var serving = Task.Run(async () =>
        {
            await Header(target.Client, timeout.Token);
            Assert.Equal(1, Volatile.Read(ref decisions));
            await Send(target.Client, "HTTP/1.1 101 Switching Protocols\r\nConnection: Upgrade\r\nUpgrade: websocket\r\nSec-WebSocket-Accept: s3pPLMBiTxaQ9kYGzzhZRbK+xOo=\r\n\r\n", timeout.Token);
            var payload = new byte[4];
            await target.Client.ReadExactlyAsync(payload, timeout.Token);
            await target.Client.WriteAsync(payload, timeout.Token);
        });
        var relay = HttpInspection.RelayAsync(caller.Server, Channel(), _ => Task.FromResult<Stream>(target.Server),
            (_, _) => { Interlocked.Increment(ref decisions); return ValueTask.CompletedTask; }, null, timeout.Token, options: Options(caller, target));
        await Send(caller.Client, "GET /ws HTTP/1.1\r\nHost: example.test\r\nConnection: Upgrade\r\nUpgrade: websocket\r\nSec-WebSocket-Version: 13\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n\r\n", timeout.Token);
        Assert.Contains("101", await Header(caller.Client, timeout.Token), StringComparison.Ordinal);
        await caller.Client.WriteAsync(new byte[] { 1, 2, 3, 4 }, timeout.Token);
        var returned = new byte[4];
        await caller.Client.ReadExactlyAsync(returned, timeout.Token);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, returned);
        await serving;
        caller.Client.Dispose(); target.Client.Dispose();
        try { await relay.WaitAsync(timeout.Token); } catch (IOException) { }
    }

    [Fact]
    public async Task Audit_failure_prevents_target_open_and_is_observed()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var caller = await Pair.CreateAsync(timeout.Token);
        var opened = 0;
        var relay = HttpInspection.RelayAsync(caller.Server, Channel(), _ => { opened++; throw new InvalidOperationException(); },
            (_, _) => throw new IOException("audit unavailable"), null, timeout.Token, options: Options(caller));
        await Send(caller.Client, "GET /ok HTTP/1.1\r\nHost: example.test\r\n\r\n", timeout.Token);
        await Assert.ThrowsAnyAsync<Exception>(async () => await relay.WaitAsync(timeout.Token));
        Assert.False(timeout.IsCancellationRequested);
        Assert.Equal(0, opened);
    }

    [Fact]
    public async Task Upstream_disconnect_does_not_reopen_or_replay_request()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var caller = await Pair.CreateAsync(timeout.Token);
        using var target = await Pair.CreateAsync(timeout.Token);
        var opens = 0;
        var serving = Task.Run(async () => { var header = await Header(target.Client, timeout.Token); target.Client.Dispose(); return header; });
        var relay = HttpInspection.RelayAsync(caller.Server, Channel(), _ => { opens++; return Task.FromResult<Stream>(target.Server); },
            (_, _) => ValueTask.CompletedTask, null, timeout.Token, options: Options(caller, target));
        await Send(caller.Client, "POST /once HTTP/1.1\r\nHost: example.test\r\nContent-Length: 0\r\n\r\n", timeout.Token);
        await Assert.ThrowsAnyAsync<Exception>(async () => await relay.WaitAsync(timeout.Token));
        Assert.False(timeout.IsCancellationRequested);
        Assert.Contains("POST /once", await serving, StringComparison.Ordinal);
        Assert.Equal(1, opens);
    }

    [Fact]
    public async Task Allowed_pipeline_preserves_chunked_response_boundary_and_checks_each_request()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var caller = await Pair.CreateAsync(timeout.Token);
        using var target = await Pair.CreateAsync(timeout.Token);
        var decisions = new List<HttpInspectionDecision>();
        var serving = Task.Run(async () =>
        {
            Assert.Contains("GET /first", await Header(target.Client, timeout.Token), StringComparison.Ordinal);
            await Send(target.Client, "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\nTrailer: X-End\r\n\r\n2\r\nok\r\n0\r\nX-End: yes\r\n\r\n", timeout.Token);
            Assert.Contains("GET /second", await Header(target.Client, timeout.Token), StringComparison.Ordinal);
            await Send(target.Client, "HTTP/1.1 202 Accepted\r\nContent-Length: 3\r\nConnection: close\r\n\r\nyes", timeout.Token);
        });
        var relay = HttpInspection.RelayAsync(caller.Server, Channel(), _ => Task.FromResult<Stream>(target.Server),
            (decision, _) => { decisions.Add(decision); return ValueTask.CompletedTask; }, null, timeout.Token, options: Options(caller, target));
        await Send(caller.Client, "GET /first HTTP/1.1\r\nHost: example.test\r\n\r\nGET /second HTTP/1.1\r\nHost: example.test\r\n\r\n", timeout.Token);
        var first = await Header(caller.Client, timeout.Token);
        Assert.Contains("200", first, StringComparison.Ordinal);
        Assert.Equal("ok", await Body(caller.Client, first, timeout.Token));
        var second = await Header(caller.Client, timeout.Token);
        Assert.Contains("202", second, StringComparison.Ordinal);
        Assert.Equal("yes", await Body(caller.Client, second, timeout.Token));
        await relay.WaitAsync(timeout.Token); await serving;
        Assert.Equal(2, decisions.Count);
        Assert.All(decisions, decision => Assert.True(decision.Allowed));
    }

    [Fact]
    public async Task Silent_first_request_is_bounded_without_truncating_approval_wait()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var caller = await Pair.CreateAsync(timeout.Token);
        var opened = 0;
        var relay = HttpInspection.RelayAsync(caller.Server, Channel(), _ => { opened++; throw new InvalidOperationException(); },
            (_, _) => ValueTask.CompletedTask, null, timeout.Token, options: Options(caller) with { SetupTimeout = TimeSpan.FromMilliseconds(200) });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await relay.WaitAsync(timeout.Token));
        Assert.Equal(0, opened);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Approval_lease_is_released_after_timeout_and_capacity_rejection_opens_no_target(bool capacityAvailable)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var caller = await Pair.CreateAsync(timeout.Token);
        var requested = 0;
        var released = 0;
        var waiting = 0;
        string? reason = null;
        var policy = new InspectionPolicy { ApproveUnsafeMethods = true, ApprovalTimeoutSeconds = 1 };
        var relay = HttpInspection.RelayAsync(caller.Server, Channel(policy), _ => throw new InvalidOperationException("No target on approval timeout."),
            (decision, _) => { reason = decision.Reason; return ValueTask.CompletedTask; }, null, timeout.Token, options: Options(caller) with
            {
                SetupTimeout = TimeSpan.FromMilliseconds(200),
                AcquireApprovalLease = _ => { requested++; return ValueTask.FromResult<IDisposable?>(capacityAvailable ? new CallbackLease(() => released++) : null); },
                OnApprovalWaiting = (decision, _) => { Assert.Equal("approval_waiting", decision.Reason); waiting++; return ValueTask.CompletedTask; }
            });
        await Send(caller.Client, "POST /pending HTTP/1.1\r\nHost: example.test\r\nContent-Length: 0\r\n\r\n", timeout.Token);
        Assert.Contains("403", await Header(caller.Client, timeout.Token), StringComparison.Ordinal);
        Assert.False(await relay.WaitAsync(timeout.Token));
        Assert.Equal(1, requested); Assert.Equal(capacityAvailable ? 1 : 0, released);
        Assert.Equal(capacityAvailable ? 1 : 0, waiting);
        Assert.Equal(capacityAvailable ? "approval_timeout" : "approval_capacity", reason);
    }

    [Fact]
    public async Task Oversized_header_is_rejected_without_opening_target()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var caller = await Pair.CreateAsync(timeout.Token);
        var relay = HttpInspection.RelayAsync(caller.Server, Channel(), _ => throw new InvalidOperationException("Oversized headers must not open a target."),
            (_, _) => ValueTask.CompletedTask, null, timeout.Token, options: Options(caller));
        await Send(caller.Client, "GET /ok HTTP/1.1\r\nHost: example.test\r\nX-Oversized: " + new string('x', HttpInspection.MaxHeaderBytes) + "\r\n\r\n", timeout.Token);
        Assert.Contains("431", await Header(caller.Client, timeout.Token), StringComparison.Ordinal);
        Assert.False(await relay.WaitAsync(timeout.Token));
    }

    [Fact]
    public async Task Malformed_chunk_cannot_turn_body_remainder_into_an_allowed_request()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var caller = await Pair.CreateAsync(timeout.Token);
        using var target = await Pair.CreateAsync(timeout.Token);
        var decisions = new List<HttpInspectionDecision>();
        var serving = Task.Run(async () =>
        {
            using var captured = new MemoryStream();
            try { await target.Client.CopyToAsync(captured, timeout.Token); } catch (IOException) { }
            return Encoding.ASCII.GetString(captured.ToArray());
        });
        var relay = HttpInspection.RelayAsync(caller.Server, Channel(new InspectionPolicy { Rules = [new InspectionRule { PathPrefix = "/secret", Action = "deny" }] }),
            _ => Task.FromResult<Stream>(target.Server), (decision, _) => { decisions.Add(decision); return ValueTask.CompletedTask; }, null,
            timeout.Token, options: Options(caller, target));
        await Send(caller.Client, "POST /body HTTP/1.1\r\nHost: example.test\r\nTransfer-Encoding: chunked\r\n\r\nZ\r\nGET /secret HTTP/1.1\r\nHost: example.test\r\n\r\n", timeout.Token);
        await Assert.ThrowsAnyAsync<Exception>(async () => await relay.WaitAsync(timeout.Token));
        Assert.False(timeout.IsCancellationRequested);
        target.Server.Dispose();
        Assert.DoesNotContain("GET /secret", await serving.WaitAsync(timeout.Token), StringComparison.Ordinal);
        Assert.Single(decisions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Http_counts_post_header_and_body_bytes_to_target_exactly_once(bool chunked)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var caller = await Pair.CreateAsync(timeout.Token);
        using var target = await Pair.CreateAsync(timeout.Token);
        var counted = 0;
        var received = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = new TaskCompletionSource<HttpInspectionResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        const string response = "HTTP/1.1 200 OK\r\nContent-Length: 5\r\nConnection: close\r\n\r\ndone!";
        var serving = Task.Run(async () =>
        {
            var bytes = await RequestBytes(target.Client, timeout.Token);
            await Send(target.Client, response, timeout.Token);
            received.TrySetResult(bytes);
        });
        var relay = HttpInspection.RelayAsync(caller.Server, Channel(), _ => Task.FromResult<Stream>(target.Server),
            (_, _) => ValueTask.CompletedTask,
            (value, _) => { result.TrySetResult(value); return ValueTask.CompletedTask; },
            timeout.Token, fromCaller: count => counted += count, options: Options(caller, target));
        const string body = "hello relay body";
        var request = chunked
            ? "POST /submit HTTP/1.1\r\nHost: example.test\r\nTransfer-Encoding: chunked\r\n\r\n5\r\nhello\r\nb\r\n relay body\r\n0\r\n\r\n"
            : "POST /submit HTTP/1.1\r\nHost: example.test\r\nContent-Length: 16\r\n\r\n" + body;
        Assert.Equal(16, Encoding.ASCII.GetByteCount(body));
        await Send(caller.Client, request, timeout.Token);
        Assert.Contains("200", await Header(caller.Client, timeout.Token), StringComparison.Ordinal);
        var payload = new byte[5];
        await caller.Client.ReadExactlyAsync(payload, timeout.Token);
        Assert.Equal("done!", Encoding.ASCII.GetString(payload));
        Assert.True(await relay.WaitAsync(timeout.Token));
        await serving;
        var targetBytes = await received.Task.WaitAsync(timeout.Token);
        var inspection = await result.Task.WaitAsync(timeout.Token);
        // The application counter must equal what the target actually received,
        // counting the forwarded header and body once each.
        Assert.True(targetBytes > Encoding.ASCII.GetByteCount("POST /submit HTTP/1.1\r\nHost: example.test\r\n\r\n"),
            $"Target received only {targetBytes} bytes.");
        Assert.Equal(targetBytes, counted);
        Assert.Equal(counted, inspection.BytesToTarget);
        Assert.Equal(Encoding.ASCII.GetByteCount(response), inspection.BytesToCaller);
    }

    [Theory]
    [InlineData("deny")]
    [InlineData("approve")]
    public async Task Pipelined_second_request_split_across_reads_is_checked_and_never_forwarded(string action)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var caller = await Pair.CreateAsync(timeout.Token);
        using var target = await Pair.CreateAsync(timeout.Token);
        var targetRequests = new List<string>();
        var decisions = new List<HttpInspectionDecision>();
        var opens = 0;
        var channel = Channel(new InspectionPolicy { ApprovalTimeoutSeconds = 2, Rules = [new InspectionRule { PathPrefix = "/secret", Action = action }] });
        var serving = Task.Run(async () =>
        {
            targetRequests.Add(await Header(target.Client, timeout.Token));
            await Send(target.Client, "HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\nok", timeout.Token);
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            wait.CancelAfter(TimeSpan.FromSeconds(3));
            try
            {
                var buffer = new byte[1024];
                while (true)
                {
                    var read = await target.Client.ReadAsync(buffer, wait.Token);
                    if (read == 0) break;
                    targetRequests.Add(Encoding.ASCII.GetString(buffer, 0, read));
                }
            }
            catch (Exception exception) when (exception is OperationCanceledException or IOException or ObjectDisposedException) { }
        });
        var relay = HttpInspection.RelayAsync(caller.Server, channel, _ => { opens++; return Task.FromResult<Stream>(target.Server); },
            (decision, _) => { decisions.Add(decision); return ValueTask.CompletedTask; }, null, timeout.Token, options: Options(caller, target));
        await Send(caller.Client, "GET /health HTTP/1.1\r\nHost: example.test\r\n\r\n", timeout.Token);
        // The second request arrives in several flushed writes: the header itself is
        // split across reads, and for the approval case the body is split as well
        // while the decision is still waiting.
        var fragments = action == "approve"
            ? new[] { "POST /sec", "ret HTTP/1.1\r\n", "Host: example.test\r\n", "Content-Length: 4\r\n\r\n", "bo", "dy" }
            : new[] { "POST /sec", "ret HTTP/1.1\r\n", "Host: example.test\r\n", "Content-Length: 4\r\n\r\nbody" };
        for (var index = 0; index < fragments.Length; index++)
        {
            await Send(caller.Client, fragments[index], timeout.Token);
            await caller.Client.FlushAsync(timeout.Token);
            if (index < fragments.Length - 1) await Task.Delay(60, timeout.Token);
        }
        Assert.Contains("200", await Header(caller.Client, timeout.Token), StringComparison.Ordinal);
        var body = new byte[2];
        await caller.Client.ReadExactlyAsync(body, timeout.Token);
        Assert.Equal("ok", Encoding.ASCII.GetString(body));
        Assert.Contains("403", await Header(caller.Client, timeout.Token), StringComparison.Ordinal);
        Assert.True(await relay.WaitAsync(timeout.Token));
        target.Server.Dispose();
        await serving;
        Assert.Equal(1, opens);
        var forwarded = string.Concat(targetRequests);
        Assert.DoesNotContain("/secret", forwarded, StringComparison.Ordinal);
        Assert.DoesNotContain("POST", forwarded, StringComparison.Ordinal);
        Assert.DoesNotContain("body", forwarded, StringComparison.Ordinal);
        Assert.Equal(2, decisions.Count);
        Assert.True(decisions[0].Allowed);
        Assert.False(decisions[1].Allowed);
        Assert.Equal(action == "deny" ? "rule_deny" : "approval_timeout", decisions[1].Reason);
    }

    private static async Task<int> RequestBytes(Stream stream, CancellationToken token)
    {
        var header = new List<byte>();
        var single = new byte[1];
        while (header.Count < 65536)
        {
            if (await stream.ReadAsync(single, token) == 0) throw new EndOfStreamException();
            header.Add(single[0]);
            if (header.Count >= 4 && header[^4] == 13 && header[^3] == 10 && header[^2] == 13 && header[^1] == 10) break;
        }
        var total = header.Count;
        var text = Encoding.ASCII.GetString(header.ToArray());
        if (text.Contains("Transfer-Encoding: chunked", StringComparison.OrdinalIgnoreCase))
        {
            while (true)
            {
                var line = new List<byte>();
                while (true)
                {
                    if (await stream.ReadAsync(single, token) == 0) throw new EndOfStreamException();
                    line.Add(single[0]); total++;
                    if (line.Count >= 2 && line[^2] == 13 && line[^1] == 10) break;
                }
                var length = Convert.ToInt32(Encoding.ASCII.GetString(line.ToArray(), 0, line.Count - 2).Split(';')[0], 16);
                if (length == 0)
                {
                    while (true)
                    {
                        var trailer = new List<byte>();
                        while (true)
                        {
                            if (await stream.ReadAsync(single, token) == 0) throw new EndOfStreamException();
                            trailer.Add(single[0]); total++;
                            if (trailer.Count >= 2 && trailer[^2] == 13 && trailer[^1] == 10) break;
                        }
                        if (trailer.Count == 2) return total;
                    }
                }
                var chunk = new byte[length];
                await stream.ReadExactlyAsync(chunk, token);
                total += length;
                var crlf = new byte[2];
                await stream.ReadExactlyAsync(crlf, token);
                Assert.Equal("\r\n", Encoding.ASCII.GetString(crlf));
                total += 2;
            }
        }
        var contentLength = text.Split("\r\n").FirstOrDefault(value => value.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
        if (contentLength is not null)
        {
            var length = int.Parse(contentLength.Split(':')[1].Trim());
            var body = new byte[length];
            await stream.ReadExactlyAsync(body, token);
            total += length;
        }
        return total;
    }

    private sealed class CallbackLease(Action release) : IDisposable { public void Dispose() => release(); }

    private static ChannelConfiguration Channel(InspectionPolicy? policy = null) =>
        new("jump", "Jump", true, "127.0.0.1", 1, "127.0.0.1", 9, 2, 5)
        { InspectionMode = "server-jump", Tags = ["app:http"], Inspection = policy };
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
    private static async Task<string> Line(Stream stream, CancellationToken token)
    {
        var bytes = new List<byte>(); var next = new byte[1];
        while (bytes.Count < 65536)
        {
            if (await stream.ReadAsync(next, token) == 0) throw new EndOfStreamException();
            bytes.Add(next[0]);
            if (bytes.Count >= 2 && bytes[^2] == 13 && bytes[^1] == 10) return Encoding.ASCII.GetString(bytes.ToArray()[..^2]);
        }
        throw new InvalidDataException();
    }
    private static async Task<string> Body(Stream stream, string header, CancellationToken token)
    {
        using var bytes = new MemoryStream();
        if (header.Contains("Transfer-Encoding: chunked", StringComparison.OrdinalIgnoreCase))
        {
            while (true)
            {
                var length = Convert.ToInt32((await Line(stream, token)).Split(';')[0], 16);
                if (length == 0) { while ((await Line(stream, token)).Length != 0) { } break; }
                var chunk = new byte[length]; await stream.ReadExactlyAsync(chunk, token); await bytes.WriteAsync(chunk, token);
                Assert.Equal("", await Line(stream, token));
            }
        }
        else
        {
            var line = header.Split("\r\n").Single(value => value.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
            var body = new byte[int.Parse(line.Split(':')[1])]; await stream.ReadExactlyAsync(body, token); await bytes.WriteAsync(body, token);
        }
        return Encoding.ASCII.GetString(bytes.ToArray());
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
