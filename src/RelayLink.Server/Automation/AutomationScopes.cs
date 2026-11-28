using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace RelayLink.Server.Automation;

/// <summary>
/// 自动化令牌的 scope 集合。固定枚举，避免自由字符串导致授权漂移。
/// 每个写 scope 隐含同名读 scope，反之不成立。
/// </summary>
public static class AutomationScopes
{
    public const string ServerRead = "server.read";
    public const string ClientsRead = "clients.read";
    public const string ClientsWrite = "clients.write";
    public const string ChannelsRead = "channels.read";
    public const string ChannelsWrite = "channels.write";
    public const string MappingsRead = "mappings.read";
    public const string MappingsWrite = "mappings.write";
    public const string SecurityRead = "security.read";
    public const string SecurityWrite = "security.write";
    public const string AuditRead = "audit.read";
    public const string HistoryRead = "history.read";
    public const string ChangesRead = "changes.read";
    public const string ChangesWrite = "changes.write";
    public const string CredentialsDelete = "credentials.delete";

    public static readonly IReadOnlyList<string> All =
    [
        ServerRead, ClientsRead, ClientsWrite, ChannelsRead, ChannelsWrite, MappingsRead, MappingsWrite,
        SecurityRead, SecurityWrite, AuditRead, HistoryRead, ChangesRead, ChangesWrite, CredentialsDelete
    ];

    // 写 scope 隐含的读 scope 映射。
    private static readonly Dictionary<string, string[]> Implied = new(StringComparer.Ordinal)
    {
        [ClientsWrite] = [ClientsRead],
        [ChannelsWrite] = [ChannelsRead],
        [MappingsWrite] = [MappingsRead],
        [SecurityWrite] = [SecurityRead],
        [ChangesWrite] = [ChangesRead]
    };

    public static bool IsKnown(string scope) => All.Contains(scope, StringComparer.Ordinal);

    /// <summary>把申请集合规范化为去重、去隐含项后的稳定顺序。</summary>
    public static string[] Normalize(IEnumerable<string>? requested)
    {
        TryNormalize(requested, out var ordered, out _);
        return ordered;
    }

    public static bool TryNormalize(IEnumerable<string>? requested, out string[] normalized, out string[] unknown)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        var unknownSet = new HashSet<string>(StringComparer.Ordinal);
        foreach (var scope in requested ?? [])
        {
            if (string.IsNullOrWhiteSpace(scope)) continue;
            var trimmed = scope.Trim();
            if (IsKnown(trimmed)) set.Add(trimmed);
            else unknownSet.Add(trimmed);
        }

        normalized = All.Where(set.Contains).ToArray();
        unknown = unknownSet.ToArray();
        return unknown.Length == 0;
    }

    /// <summary>
    /// 判断 granted 是否满足 required。读 scope 可由同名写 scope 覆盖；
    /// 写 scope 不能由同名读 scope 覆盖。这本就是 <see cref="Satisfies"/> 的语义，
    /// 保留单一入口避免两套判定分叉。
    /// </summary>
    public static bool Covers(IReadOnlyCollection<string> granted, string required) => Satisfies(granted, required);

    public static bool Satisfies(IReadOnlyCollection<string> granted, string required)
    {
        if (granted.Contains(required, StringComparer.Ordinal)) return true;
        // required 是读 scope 时，同名写 scope 也覆盖它。
        foreach (var (write, reads) in Implied)
        {
            if (reads.Contains(required, StringComparer.Ordinal) && granted.Contains(write, StringComparer.Ordinal)) return true;
        }

        return false;
    }

    /// <summary>
    /// 批准时只允许缩小，不允许扩大。支持将写 scope 缩小为对应的读 scope。
    /// </summary>
    public static bool IsSubsetOf(IEnumerable<string> approved, IEnumerable<string> requested)
    {
        var requestedArray = requested.ToArray();
        return approved.All(scope => Satisfies(requestedArray, scope));
    }
}
