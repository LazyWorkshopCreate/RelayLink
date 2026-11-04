using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.DevTunnels.Ssh;
using Microsoft.DevTunnels.Ssh.Events;
using Microsoft.DevTunnels.Ssh.Messages;
using RelayLink.Server.Configuration;
using RelayLink.Server.Runtime;

namespace RelayLink.IntegrationTests;

#if PEER_TLS_TESTS
public sealed class SshInspectionProcessTests
{
    [Theory]
    [InlineData("server-peer")]
    [InlineData("server-jump")]
    public async Task Active_ssh_data_outlives_setup_and_counts_application_bytes(string mode)
    {
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        using var hostKey = SshAlgorithms.PublicKey.ECDsaSha2Nistp256.GenerateKeyPair();
        using var target = new TcpListener(IPAddress.Loopback, 0);
        target.Start();
        var port = ((IPEndPoint)target.LocalEndpoint).Port;
        var targetComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var serving = Task.Run(async () =>
        {
            using var socket = await target.AcceptTcpClientAsync(lifetime.Token);
            using var server = new SshServerSession(SshSessionConfiguration.Default, QuietTrace());
            server.Credentials = new SshServerCredentials(hostKey);
            server.Authenticating += (_, args) => args.AuthenticationTask = Task.FromResult<ClaimsPrincipal?>(
                args.AuthenticationType == SshAuthenticationType.ClientPassword && args.Username == "target" && args.Password == "test-target-password"
                    ? new ClaimsPrincipal(new ClaimsIdentity("test")) : null);
            server.ChannelOpening += (_, args) =>
            {
                args.Channel.Request += (_, request) =>
                {
                    request.IsAuthorized = request.RequestType == ChannelRequestTypes.Command;
                    if (!request.IsAuthorized) return;
                    request.ResponseContinuation = async _ =>
                    {
                        try
                        {
                            using var stream = new SshStream(args.Channel);
                            var input = new byte[5];
                            await stream.ReadExactlyAsync(input, lifetime.Token);
                            Assert.Equal("input"u8.ToArray(), input);
                            for (var i = 0; i < 40; i++)
                            {
                                await args.Channel.SendAsync(new byte[] { (byte)'x' }, lifetime.Token);
                                await Task.Delay(100, lifetime.Token);
                            }
                            await args.Channel.CloseAsync(lifetime.Token);
                            targetComplete.TrySetResult();
                        }
                        catch (Exception exception) { targetComplete.TrySetException(exception); }
                    };
                };
            };
            await server.ConnectAsync(socket.GetStream(), lifetime.Token);
            await targetComplete.Task.WaitAsync(lifetime.Token);
        });
        var policy = new InspectionPolicy { Ssh = new SshInspectionPolicy
        { TargetHostKeySha256 = "SHA256:" + Convert.ToBase64String(SHA256.HashData(hostKey.GetPublicKeyBytes().ToArray())).TrimEnd('=') } };
        using var fixture = new PeerConnectionTests.Fixture();
        var channelId = mode == "server-peer" ? "private" : "jump";
        await fixture.StartAsync(inspectionMode: mode, peerApplication: "ssh", peerTargetPort: port, peerInspection: policy,
            jumpApplication: "ssh", jumpTargetPort: port, jumpInspection: policy, openTimeoutSeconds: 2, tcpIdleTimeoutSeconds: 2,
            prepareServer: async path =>
            {
                var store = CredentialStore.Open(path);
                await store.SaveAsync("visited", channelId, "door", "door", "front-door", "password", "test-door-password"u8.ToArray(), lifetime.Token);
                await store.SaveAsync("visited", channelId, "target", "target", "target", "password", "test-target-password"u8.ToArray(), lifetime.Token);
            });
        await fixture.WaitOnlineAsync(lifetime.Token);
        using var caller = new TcpClient();
        await caller.ConnectAsync(IPAddress.Loopback, mode == "server-peer" ? fixture.LocalPort : fixture.ProxyPort, lifetime.Token);
        using var client = new SshClientSession(SshSessionConfiguration.Default, QuietTrace());
        client.Authenticating += (_, args) =>
        {
            if (args.AuthenticationType == SshAuthenticationType.ServerPublicKey)
                args.AuthenticationTask = Task.FromResult<ClaimsPrincipal?>(new ClaimsPrincipal(new ClaimsIdentity("test-server")));
        };
        await client.ConnectAsync(caller.GetStream(), lifetime.Token);
        Assert.True(await client.AuthenticateAsync(new SshClientCredentials("door", "test-door-password"), lifetime.Token));
        var channel = await client.OpenChannelAsync(lifetime.Token);
        Assert.True(await channel.RequestAsync(new CommandRequestMessage("slow") { WantReply = true }, lifetime.Token));
        using var application = new SshStream(channel);
        var clock = Stopwatch.StartNew();
        await application.WriteAsync("input"u8.ToArray(), lifetime.Token);
        var response = new byte[40];
        await application.ReadExactlyAsync(response, lifetime.Token);
        Assert.Equal(Enumerable.Repeat((byte)'x', 40), response);
        Assert.True(clock.Elapsed > TimeSpan.FromSeconds(3));
        await serving;
        using var http = new HttpClient();
        var status = await http.GetFromJsonAsync<JsonElement>($"http://{fixture.DashboardAddress}/api/v1/clients/visited/channels", lifetime.Token);
        var counters = status.GetProperty("channels").EnumerateArray().Single(item => item.GetProperty("channelId").GetString() == channelId);
        Assert.Equal(5, counters.GetProperty("applicationToTarget").GetInt64());
        Assert.Equal(40, counters.GetProperty("applicationToCaller").GetInt64());
        Assert.Equal(0, counters.GetProperty("peerCiphertextToTarget").GetInt64());
    }

    private static TraceSource QuietTrace() => new("inspection-process-test") { Switch = { Level = SourceLevels.Off } };
}
#endif
