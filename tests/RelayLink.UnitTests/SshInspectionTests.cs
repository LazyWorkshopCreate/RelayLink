using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.DevTunnels.Ssh;
using Microsoft.DevTunnels.Ssh.Algorithms;
using Microsoft.DevTunnels.Ssh.Events;
using Microsoft.DevTunnels.Ssh.Messages;
using RelayLink.Server.Configuration;
using RelayLink.Server.Runtime;

namespace RelayLink.UnitTests;

public sealed class SshInspectionTests
{
    [Fact]
    public async Task Ssh_exec_is_bridged_and_forwarding_does_not_open_a_target_session()
    {
        var directory = Directory.CreateTempSubdirectory("relaylink-ssh-").FullName;
        var configuration = Path.Combine(directory, "server.json");
        await File.WriteAllTextAsync(configuration, "{}");
        var executions = 0;
        using var targetListener = new TcpListener(IPAddress.Loopback, 0);
        targetListener.Start();
        var targetPort = ((IPEndPoint)targetListener.LocalEndpoint).Port;
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var targetHost = SshAlgorithms.PublicKey.ECDsaSha2Nistp256.GenerateKeyPair();
        var serving = Task.Run(() => ServeTargetAsync(targetListener, targetHost, () => Interlocked.Increment(ref executions), lifetime.Token));
        try
        {
            var store = CredentialStore.Open(configuration);
            await store.SaveAsync("visited", "jump", "door", "door", "front-door", "password", "door-secret"u8.ToArray(), CancellationToken.None);
            await store.SaveAsync("visited", "jump", "root", "root", "target", "password", "target-secret"u8.ToArray(), CancellationToken.None);
            var channel = new ChannelConfiguration("jump", "Jump", true, "127.0.0.1", 1, "127.0.0.1", targetPort, 1, 5)
            {
                InspectionMode = "server-jump",
                Tags = ["app:ssh"],
                Inspection = new InspectionPolicy { Ssh = new SshInspectionPolicy
                { TargetHostKeySha256 = "SHA256:" + Convert.ToBase64String(SHA256.HashData(targetHost.GetPublicKeyBytes().ToArray())).TrimEnd('=') } }
            };

            var result = await ExecAsync(store, channel, targetPort, "door", "door-secret", "echo ok", lifetime.Token);
            Assert.Contains("ok", result, StringComparison.Ordinal);
            Assert.Equal(1, Volatile.Read(ref executions));

            using var caller = await AcceptPairAsync(lifetime.Token);
            var relay = SshInspection.RelayAsync(caller.Server, _ => throw new InvalidOperationException("Forwarding must not open the target."), "visited", "jump", channel, store, null, Guid.NewGuid(), lifetime.Token);
            var client = new SshClientSession(SshSessionConfiguration.Default, QuietTrace());
            client.Authenticating += (_, args) =>
            {
                if (args.AuthenticationType == SshAuthenticationType.ServerPublicKey)
                    args.AuthenticationTask = Task.FromResult<ClaimsPrincipal?>(new ClaimsPrincipal(new ClaimsIdentity("test")));
            };
            await client.ConnectAsync(caller.Client, lifetime.Token);
            Assert.True(await client.AuthenticateAsync(new SshClientCredentials("door", "door-secret"), lifetime.Token));
            await Assert.ThrowsAsync<SshChannelException>(() => client.OpenChannelAsync("direct-tcpip", lifetime.Token));
            client.Dispose();
            await relay;
            Assert.Equal(1, Volatile.Read(ref executions));
        }
        finally
        {
            lifetime.Cancel();
            targetListener.Stop();
            try { await serving; } catch (OperationCanceledException) { }
            try { Directory.Delete(directory, true); } catch (IOException) { }
        }
    }

    private static async Task<string> ExecAsync(CredentialStore store, ChannelConfiguration channel, int targetPort, string username, string password, string command, CancellationToken cancellationToken)
    {
        using var pair = await AcceptPairAsync(cancellationToken);
        var relay = SshInspection.RelayAsync(pair.Server, async token =>
        {
            var target = new TcpClient();
            await target.ConnectAsync(IPAddress.Loopback, targetPort, token);
            return target.GetStream();
        }, "visited", "jump", channel, store, null, Guid.NewGuid(), cancellationToken);
        var client = new SshClientSession(SshSessionConfiguration.Default, QuietTrace());
        client.Authenticating += (_, args) =>
        {
            if (args.AuthenticationType == SshAuthenticationType.ServerPublicKey)
                args.AuthenticationTask = Task.FromResult<ClaimsPrincipal?>(new ClaimsPrincipal(new ClaimsIdentity("test")));
        };
        await client.ConnectAsync(pair.Client, cancellationToken);
        Assert.True(await client.AuthenticateAsync(new SshClientCredentials(username, password), cancellationToken));
        var session = await client.OpenChannelAsync(cancellationToken);
        Assert.True(await session.RequestAsync(new CommandRequestMessage(command) { WantReply = true }, cancellationToken));
        using var stream = new SshStream(session);
        var buffer = new byte[32];
        var read = await stream.ReadAsync(buffer, cancellationToken);
        client.Dispose();
        await relay;
        return Encoding.UTF8.GetString(buffer, 0, read);
    }

    private static async Task ServeTargetAsync(TcpListener listener, IKeyPair host, Action executed, CancellationToken cancellationToken)
    {
        var trace = QuietTrace();
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient accepted;
            try { accepted = await listener.AcceptTcpClientAsync(cancellationToken); }
            catch (OperationCanceledException) { break; }
            var server = new SshServerSession(SshSessionConfiguration.Default, trace);
            server.Credentials = new SshServerCredentials(host);
            server.Authenticating += (_, args) =>
            {
                var acceptedPassword = args.AuthenticationType == SshAuthenticationType.ClientPassword && args.Username == "root" && args.Password == "target-secret";
                args.AuthenticationTask = Task.FromResult<ClaimsPrincipal?>(acceptedPassword ? new ClaimsPrincipal(new ClaimsIdentity("target")) : null);
            };
            server.ChannelOpening += (_, args) =>
            {
                args.Channel.Request += (_, request) =>
                {
                    request.IsAuthorized = request.RequestType == ChannelRequestTypes.Command;
                    if (!request.IsAuthorized) return;
                    executed();
                    request.ResponseContinuation = async _ =>
                    {
                        await args.Channel.SendAsync("ok\n"u8.ToArray(), CancellationToken.None);
                        await args.Channel.CloseAsync(CancellationToken.None);
                    };
                };
            };
            try { await server.ConnectAsync(accepted.GetStream(), cancellationToken); }
            catch (Exception exception) when (exception is IOException or OperationCanceledException or SshConnectionException) { }
        }
    }

    private static async Task<ConnectedPair> AcceptPairAsync(CancellationToken cancellationToken)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var client = new TcpClient();
        var connect = client.ConnectAsync(IPAddress.Loopback, port, cancellationToken);
        var accepted = await listener.AcceptTcpClientAsync(cancellationToken);
        await connect;
        listener.Stop();
        return new ConnectedPair(client.GetStream(), accepted.GetStream());
    }

    private static TraceSource QuietTrace() => new("test") { Switch = { Level = SourceLevels.Off } };

    private sealed record ConnectedPair(Stream Client, Stream Server) : IDisposable
    {
        public void Dispose()
        {
            Client.Dispose();
            Server.Dispose();
        }
    }
}
