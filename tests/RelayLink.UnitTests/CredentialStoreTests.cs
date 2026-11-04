using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RelayLink.Server.Runtime;

namespace RelayLink.UnitTests;

public sealed class CredentialStoreTests
{
    [Fact]
    public async Task Saved_credentials_list_metadata_only_and_reject_a_tampered_store()
    {
        var directory = Directory.CreateTempSubdirectory("relaylink-credentials-").FullName;
        var configuration = Path.Combine(directory, "server.json");
        File.WriteAllText(configuration, "{}");
        try
        {
            var store = CredentialStore.Open(configuration);
            var secret = "target-password"u8.ToArray();
            var saved = await store.SaveAsync("visited", "private", "shell", "root", "target", "password", secret, CancellationToken.None);
            CryptographicOperations.ZeroMemory(secret);
            Assert.Equal("shell", saved.CredentialId);
            var listed = JsonSerializer.Serialize(await store.ListAsync("visited", "private", CancellationToken.None));
            Assert.DoesNotContain("target-password", listed, StringComparison.Ordinal);
            Assert.Contains("shell", listed, StringComparison.Ordinal);

            using (var opened = await store.OpenAsync("visited", "private", "shell", CancellationToken.None))
            {
                Assert.NotNull(opened);
                Assert.Equal("target-password", Encoding.UTF8.GetString(opened.Secret));
            }

            var replaced = "rotated"u8.ToArray();
            await store.SaveAsync("visited", "private", "shell", "root", "target", "password", replaced, CancellationToken.None);
            using (var opened = await store.OpenAsync("visited", "private", "shell", CancellationToken.None))
                Assert.Equal("rotated", Encoding.UTF8.GetString(opened!.Secret));

            var storePath = Path.Combine(CredentialStore.DirectoryFor(configuration), "store");
            var packed = Convert.FromBase64String(File.ReadAllText(storePath));
            packed[^1] ^= 0x01;
            File.WriteAllText(storePath, Convert.ToBase64String(packed));
            await Assert.ThrowsAsync<CredentialStoreException>(() => store.ListAsync("visited", "private", CancellationToken.None));
        }
        finally { try { Directory.Delete(directory, true); } catch (IOException) { } }
    }

    [Fact]
    public async Task Store_rejects_invalid_material_and_a_missing_key()
    {
        var directory = Directory.CreateTempSubdirectory("relaylink-credentials-").FullName;
        var configuration = Path.Combine(directory, "server.json");
        await File.WriteAllTextAsync(configuration, "{}");
        try
        {
            var store = CredentialStore.Open(configuration);
            await Assert.ThrowsAsync<CredentialStoreException>(() => store.SaveAsync("visited", "private", "shell", "root", "browser", "password", "secret"u8.ToArray(), CancellationToken.None));
            for (var index = 0; index < CredentialStore.MaxPerChannel; index++)
                await store.SaveAsync("visited", "private", $"id{index:00}", "root", "front-door", "password", "secret"u8.ToArray(), CancellationToken.None);
            await Assert.ThrowsAsync<CredentialStoreException>(() => store.SaveAsync("visited", "private", "overflow", "root", "front-door", "password", "secret"u8.ToArray(), CancellationToken.None));
            Assert.True(await store.DeleteAsync("visited", "private", "id00", CancellationToken.None));
            Assert.Equal(CredentialStore.MaxPerChannel - 1, (await store.ListAsync("visited", "private", CancellationToken.None)).Count);

            File.Delete(Path.Combine(CredentialStore.DirectoryFor(configuration), "key"));
            Assert.Throws<CredentialStoreException>(() => CredentialStore.Open(configuration));
        }
        finally { try { Directory.Delete(directory, true); } catch (IOException) { } }
    }
}
