namespace RelayLink.Cli;

/// <summary>
/// 全局选项与命令定位。子命令专用的参数（--type/--payload/--tag 等）由各子命令
/// 从 rest 里自行解析，不在这里吃掉——否则 changes submit 的参数会被全局解析吞掉。
/// </summary>
public sealed class CliOptions
{
    public string? Profile { get; private set; }
    public string? Entry { get; private set; }
    public int? DashboardPort { get; private set; }
    public bool Json { get; private set; }
    public bool DryRun { get; private set; }
    public bool Yes { get; private set; }
    public bool Wait { get; private set; }
    public bool Verbose { get; private set; }
    public bool Help { get; private set; }
    public int? TimeoutSeconds { get; private set; }
    public string? Spki { get; private set; }
    public bool AcceptSpki { get; private set; }
    public bool Force { get; private set; }
    public string? ResumeRequestId { get; private set; }
    public string? ResumeUserCode { get; private set; }
    public int? ApiVersion { get; private set; }

    public string? Subject { get; private set; }
    public List<string> Scopes { get; } = [];
    public List<string> Positional { get; } = [];

    /// <summary>第一个非选项 token 的索引（即子命令），由 TryParse 记录。</summary>
    public int CommandIndex { get; private set; } = -1;

    /// <summary>全局选项及其值占用的参数索引。子命令 rest 构造时排除它们。</summary>
    public HashSet<int> GlobalOptionIndices { get; } = [];

    /// <summary>全局选项的「名 → 是否带值」表。带值的选项会吞掉下一个 token。</summary>
    private static readonly Dictionary<string, bool> GlobalOptions = new(StringComparer.Ordinal)
    {
        ["--profile"] = true,
        ["--entry"] = true,
        ["--dashboard-port"] = true,
        ["--json"] = false,
        ["--dry-run"] = false,
        ["--yes"] = false,
        ["-y"] = false,
        ["--wait"] = false,
        ["--verbose"] = false,
        ["--help"] = false,
        ["-h"] = false,
        ["--timeout"] = true,
        ["--scope"] = true,
        ["--subject"] = true,
        ["--spki"] = true,
        ["--accept-spki"] = false,
        ["--request"] = true,
        ["--code"] = true,
        ["--force"] = false,
        ["--api-version"] = true
    };

    public static bool TryParse(string[] args, out CliOptions options, out string? error)
    {
        options = new CliOptions();
        error = null;
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];

            if (!argument.StartsWith('-') || argument.Length <= 1)
            {
                // 子命令与其余位置参数：只把第一个记为命令名。
                if (options.CommandIndex < 0) options.CommandIndex = index;
                options.Positional.Add(argument);
                continue;
            }

            // 未知选项不直接报错：子命令专用参数（--type/--payload/--tag 等）
            // 由各子命令从 rest 里解析。全局选项照常处理，未知选项按位置参数放行。
            if (!GlobalOptions.TryGetValue(argument, out var takesValue))
            {
                if (options.CommandIndex < 0) options.CommandIndex = index;
                options.Positional.Add(argument);
                continue;
            }

            options.GlobalOptionIndices.Add(index);
            string? value = null;
            if (takesValue)
            {
                if (++index >= args.Length)
                {
                    error = $"选项 {argument} 缺少值。";
                    return false;
                }

                value = args[index];
                options.GlobalOptionIndices.Add(index);
            }

            switch (argument)
            {
                case "--profile": options.Profile = value; break;
                case "--entry": options.Entry = value; break;
                case "--dashboard-port":
                    if (!int.TryParse(value, out var parsed)) { error = "--dashboard-port 必须是整数。"; return false; }
                    options.DashboardPort = parsed;
                    break;
                case "--json": options.Json = true; break;
                case "--dry-run": options.DryRun = true; break;
                case "--yes" or "-y": options.Yes = true; break;
                case "--wait": options.Wait = true; break;
                case "--verbose": options.Verbose = true; break;
                case "--help" or "-h": options.Help = true; break;
                case "--timeout":
                    if (!int.TryParse(value, out var seconds) || seconds <= 0) { error = "--timeout 必须是正整数秒。"; return false; }
                    options.TimeoutSeconds = seconds;
                    break;
                case "--scope":
                    if (value is not null) options.Scopes.Add(value);
                    break;
                case "--subject": options.Subject = value; break;
                case "--spki": options.Spki = value; break;
                case "--accept-spki": options.AcceptSpki = true; break;
                case "--request": options.ResumeRequestId = value; break;
                case "--code": options.ResumeUserCode = value; break;
                case "--force": options.Force = true; break;
                case "--api-version":
                    if (!int.TryParse(value, out var apiVersion) || apiVersion <= 0) { error = "--api-version 必须是正整数。"; return false; }
                    options.ApiVersion = apiVersion;
                    break;
            }
        }

        return true;
    }

    /// <summary>非 TTY 时不阻塞等待交互确认；这是 agent 环境的基本要求。</summary>
    public static bool IsInteractive => !Console.IsInputRedirected && !Console.IsOutputRedirected;
}
