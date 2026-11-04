using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RelayLink.Server.Runtime;

public sealed record CredentialMetadata(string CredentialId, string Username, string Purpose, string Type, DateTimeOffset UpdatedUtc);

public sealed class CredentialMaterial : IDisposable
{
    public required string CredentialId { get; init; }
    public required string Username { get; init; }
    public required string Purpose { get; init; }
    public required string Type { get; init; }
    public required byte[] Secret { get; init; }
    public void Dispose() => CryptographicOperations.ZeroMemory(Secret);
}

public sealed class CredentialStoreException(string message, Exception? inner = null) : Exception(message, inner);

public sealed record CredentialWriteRequest(string Username, string Purpose, string Type, string? Secret);

public sealed partial class CredentialStore
{
    public const int MaxPerChannel = 32;
    private static readonly Regex Identifier = new("^[a-z0-9][a-z0-9_-]{0,63}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly string keyPath;
    private readonly string storePath;
    private readonly SemaphoreSlim gate = new(1, 1);
    private byte[] key = [];
    private string? initializationFailure;
    private bool storeExpected;

    private CredentialStore(string directory, bool tolerateFailure = false)
    {
        keyPath = Path.Combine(directory, "key");
        storePath = Path.Combine(directory, "store");
        try
        {
            Directory.CreateDirectory(directory);
            var existingKey = File.Exists(keyPath);
            key = LoadOrCreateKey();
            storeExpected = File.Exists(storePath);
            if (!storeExpected)
            {
                if (existingKey) throw new CredentialStoreException("Credential key exists without its store; restore the matching store before restarting.");
                WriteAsync(new Document(), CancellationToken.None).GetAwaiter().GetResult();
            }
        }
        catch (Exception exception) when (tolerateFailure && exception is IOException or UnauthorizedAccessException or FormatException or CredentialStoreException or System.Security.SecurityException)
        {
            initializationFailure = "Credential store initialization failed; restore its key and store together, then restart the service.";
        }
    }

    public static string DirectoryFor(string serverConfigurationPath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(serverConfigurationPath))!, "credentials");

    public static CredentialStore Open(string serverConfigurationPath) => new(DirectoryFor(serverConfigurationPath));
    public static CredentialStore OpenForService(string serverConfigurationPath) => new(DirectoryFor(serverConfigurationPath), tolerateFailure: true);

    public async Task CheckAvailableAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try { _ = Read(); }
        finally { gate.Release(); }
    }

    public async Task<IReadOnlyList<CredentialMetadata>> ListAsync(string clientId, string channelId, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var document = Read();
            EnsureNotDeleting(document, clientId, channelId);
            return document.Credentials
                .Where(item => item.ClientId == clientId && item.ChannelId == channelId)
                .OrderBy(item => item.CredentialId, StringComparer.Ordinal)
                .Select(Metadata)
                .ToArray();
        }
        finally { gate.Release(); }
    }

    public async Task<CredentialMetadata> SaveAsync(string clientId, string channelId, string credentialId, string username, string purpose, string type, ReadOnlyMemory<byte> secret, CancellationToken cancellationToken)
    {
        Validate(clientId, channelId, credentialId, username, purpose, type, secret.Span);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var document = Read();
            EnsureNotDeleting(document, clientId, channelId);
            var existing = document.Credentials.FindIndex(item => item.ClientId == clientId && item.ChannelId == channelId && item.CredentialId == credentialId);
            if (existing < 0 && document.Credentials.Count(item => item.ClientId == clientId && item.ChannelId == channelId) >= MaxPerChannel)
                throw new CredentialStoreException($"Channel {clientId}/{channelId} already has {MaxPerChannel} credentials.");
            var stored = new StoredCredential
            {
                ClientId = clientId,
                ChannelId = channelId,
                CredentialId = credentialId,
                Username = username,
                Purpose = purpose,
                Type = type,
                Secret = Convert.ToBase64String(secret.Span),
                UpdatedUtc = DateTimeOffset.UtcNow
            };
            if (existing < 0) document.Credentials.Add(stored);
            else document.Credentials[existing] = stored;
            await WriteAsync(document, cancellationToken);
            return Metadata(stored);
        }
        finally { gate.Release(); }
    }

    public async Task<bool> DeleteAsync(string clientId, string channelId, string credentialId, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var document = Read();
            EnsureNotDeleting(document, clientId, channelId);
            var removed = document.Credentials.RemoveAll(item => item.ClientId == clientId && item.ChannelId == channelId && item.CredentialId == credentialId);
            if (removed == 0) return false;
            await WriteAsync(document, cancellationToken);
            return true;
        }
        finally { gate.Release(); }
    }

    internal async Task<CredentialMaterial?> OpenAsync(string clientId, string channelId, string credentialId, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var document = Read();
            EnsureNotDeleting(document, clientId, channelId);
            var stored = document.Credentials.FirstOrDefault(item => item.ClientId == clientId && item.ChannelId == channelId && item.CredentialId == credentialId);
            if (stored is null) return null;
            return new CredentialMaterial
            {
                CredentialId = stored.CredentialId,
                Username = stored.Username,
                Purpose = stored.Purpose,
                Type = stored.Type,
                Secret = Convert.FromBase64String(stored.Secret)
            };
        }
        finally { gate.Release(); }
    }

    private byte[] LoadOrCreateKey()
    {
        if (File.Exists(keyPath))
        {
            var decoded = Convert.FromBase64String(File.ReadAllText(keyPath).Trim());
            if (decoded.Length != 32) throw new CredentialStoreException("Credential key is invalid.");
            return decoded;
        }

        if (File.Exists(storePath)) throw new CredentialStoreException("Credential store exists without its key.");
        var created = RandomNumberGenerator.GetBytes(32);
        SecretFilePermissions.WriteTextAsync(keyPath, Convert.ToBase64String(created), overwrite: false, CancellationToken.None).GetAwaiter().GetResult();
        return created;
    }

    private Document Read()
    {
        try { return ReadCore(); }
        catch (CredentialStoreException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException or System.Security.SecurityException)
        { throw new CredentialStoreException("Credential store could not be read.", exception); }
    }

    private Document ReadCore()
    {
        if (initializationFailure is not null) throw new CredentialStoreException(initializationFailure);
        if (!File.Exists(keyPath)) throw new CredentialStoreException("Credential key is missing; restore the matching key and store.");
        var persistedKey = Convert.FromBase64String(File.ReadAllText(keyPath).Trim());
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(persistedKey, key))
                throw new CredentialStoreException("Credential key changed; restore the matching key and store before restarting.");
        }
        finally { CryptographicOperations.ZeroMemory(persistedKey); }
        if (!File.Exists(storePath))
        {
            if (storeExpected) throw new CredentialStoreException("Credential store disappeared; refusing to create an empty replacement.");
            return new Document();
        }
        storeExpected = true;
        byte[] packed;
        try { packed = Convert.FromBase64String(File.ReadAllText(storePath).Trim()); }
        catch (FormatException exception) { throw new CredentialStoreException("Credential store could not be read.", exception); }
        if (packed.Length < 1 + 12 + 16 || packed[0] != 1) throw new CredentialStoreException("Credential store could not be read.");
        var nonce = packed.AsSpan(1, 12);
        var tag = packed.AsSpan(13, 16);
        var ciphertext = packed.AsSpan(29);
        var plaintext = new byte[ciphertext.Length];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
            var document = JsonSerializer.Deserialize<Document>(plaintext, JsonOptions) ?? new Document();
            document.Credentials ??= [];
            document.HostKeys ??= [];
            document.Operations ??= [];
            document.Deletions ??= [];
            return document;
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException)
        {
            throw new CredentialStoreException("Credential store could not be read.", exception);
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    private async Task WriteAsync(Document document, CancellationToken cancellationToken)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(nonce, plaintext, ciphertext, tag);
            var packed = new byte[1 + nonce.Length + tag.Length + ciphertext.Length];
            packed[0] = 1;
            nonce.CopyTo(packed, 1);
            tag.CopyTo(packed, 13);
            ciphertext.CopyTo(packed, 29);
            await SecretFilePermissions.WriteTextAsync(storePath, Convert.ToBase64String(packed), overwrite: true, cancellationToken);
            storeExpected = true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(ciphertext);
        }
    }

    private static void Validate(string clientId, string channelId, string credentialId, string username, string purpose, string type, ReadOnlySpan<byte> secret)
    {
        if (!Identifier.IsMatch(clientId) || !Identifier.IsMatch(channelId) || !Identifier.IsMatch(credentialId))
            throw new CredentialStoreException("Credential identifiers must be 1–64 characters: lowercase a–z, digits, '_' or '-', starting with a letter or digit.");
        if (username is not { Length: > 0 and <= 128 } || username.Any(character => char.IsControl(character)))
            throw new CredentialStoreException("Credential username is invalid.");
        if (purpose is not ("target" or "front-door")) throw new CredentialStoreException("Credential purpose must be target or front-door.");
        if (type is not ("password" or "private-key")) throw new CredentialStoreException("Credential type must be password or private-key.");
        var limit = type == "password" ? 1024 : 16 * 1024;
        if (secret.Length is 0 || secret.Length > limit || secret.Contains((byte)0))
            throw new CredentialStoreException("Credential secret is empty, too large, or contains a NUL.");
    }

    internal async Task<byte[]> GetOrCreateHostKeyAsync(string clientId, string channelId, Func<byte[]> create, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var document = Read();
            EnsureNotDeleting(document, clientId, channelId);
            var existing = document.HostKeys.FirstOrDefault(item => item.ClientId == clientId && item.ChannelId == channelId);
            if (existing is not null) return Convert.FromBase64String(existing.PrivateKey);
            var created = create();
            document.HostKeys.Add(new StoredHostKey { ClientId = clientId, ChannelId = channelId, PrivateKey = Convert.ToBase64String(created) });
            await WriteAsync(document, cancellationToken);
            return created;
        }
        finally { gate.Release(); }
    }

    private static CredentialMetadata Metadata(StoredCredential stored) =>
        new(stored.CredentialId, stored.Username, stored.Purpose, stored.Type, stored.UpdatedUtc);

    private sealed class Document
    {
        public List<StoredCredential> Credentials { get; set; } = [];
        public List<StoredHostKey> HostKeys { get; set; } = [];
        public List<CredentialOperation> Operations { get; set; } = [];
        public List<CredentialDeletion> Deletions { get; set; } = [];
    }

    private sealed class StoredHostKey
    {
        public string ClientId { get; set; } = "";
        public string ChannelId { get; set; } = "";
        public string PrivateKey { get; set; } = "";
    }

    private sealed class StoredCredential
    {
        public string ClientId { get; set; } = "";
        public string ChannelId { get; set; } = "";
        public string CredentialId { get; set; } = "";
        public string Username { get; set; } = "";
        public string Purpose { get; set; } = "";
        public string Type { get; set; } = "";
        public string Secret { get; set; } = "";
        public DateTimeOffset UpdatedUtc { get; set; }
    }
}
