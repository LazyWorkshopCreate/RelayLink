using System.Text.Json;
using RelayLink.AutomationClient;
using ApiClient = RelayLink.AutomationClient.AutomationClient;

namespace RelayLink.Cli;

/// <summary>
/// 命令分发。每个动作映射到明确 scope；越权时停止并提示如何申请，不改参数重试。
/// </summary>
public sealed class CliApp(CliOptions options, TextWriter output, TextWriter error)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (options.Help || args.Length == 0)
        {
            ShowHelp();
            return options.Help ? ExitCodes.Success : ExitCodes.Usage;
        }

        // 全局选项可出现在任何位置（含子命令之前）。TryParse 已把第一个
        // 非选项 token 记为命令（CommandIndex），此处按索引取命令名。
        if (options.CommandIndex < 0 || options.CommandIndex >= args.Length)
        {
            ShowHelp();
            return ExitCodes.Usage;
        }

        var command = args[options.CommandIndex];
        // rest 排除全局选项及其值，只留子命令名与其专用参数。
        var rest = args
            .Select((argument, index) => (argument, index))
            .Where(pair => pair.index != options.CommandIndex && !options.GlobalOptionIndices.Contains(pair.index))
            .Select(pair => pair.argument)
            .ToArray();
        return command switch
        {
            "pair" => await PairAsync(rest, cancellationToken),
            "auth" => await AuthAsync(rest, cancellationToken),
            "doctor" => await DoctorAsync(cancellationToken),
            "server" => await ServerAsync(rest, cancellationToken),
            "clients" => await ClientsAsync(rest, cancellationToken),
            "channels" => await ChannelsAsync(rest, cancellationToken),
            "mappings" => await MappingsAsync(rest, cancellationToken),
            "changes" => await ChangesAsync(rest, cancellationToken),
            "skill" => await SkillAsync(rest, cancellationToken),
            _ => Usage($"未知命令：{command}")
        };
    }

    private int Usage(string message)
    {
        error.WriteLine(message);
        ShowHelp();
        return ExitCodes.Usage;
    }

    private void ShowHelp() => output.WriteLine("""
        relaylink —— RelayLink 自动化管理面 CLI

        用法：
          relaylink pair [--wait] [--scope <s>]... [--subject <name>]
          relaylink pair status|cancel
          relaylink auth whoami|logout|profiles|trust --reset
          relaylink doctor
          relaylink server health|overview|version
          relaylink clients ls
          relaylink channels ls <clientId>
          relaylink channels set <clientId> --channel <id> [--display-name <v>] [--tag <v>]...
          relaylink mappings ls <clientId>
          relaylink changes submit --type <t> --target <clientId> [--channel <id>] --payload <file>
          relaylink changes ls|get <id>|cancel <id>
          relaylink skill install --host <host> [--force]

        全局选项：
          --profile <name>  --entry <host:port>  --dashboard-port <n>
          --json  --dry-run  --yes  --wait  --timeout <s>  --verbose
          --spki <sha256>  --accept-spki

        退出码：0 成功 2 用法 3 未授权 4 scope 不足 5 需人工批准
                6 服务端拒绝 7 审计/存储不可用 8 网络或证书固定失败 9 版本不兼容
        """);

    // ---------------- 配对 ----------------

    private async Task<int> PairAsync(string[] rest, CancellationToken cancellationToken)
    {
        var sub = rest.FirstOrDefault();
        if (sub is "status") return await PairStatusAsync(cancellationToken);
        if (sub is "cancel") return await PairCancelAsync(cancellationToken);

        var configuration = CliStorage.LoadConfiguration();
        var profile = ResolveProfile(configuration, out var profileError);
        if (profile is null) return profileError;

        // 续接模式：--request + --code 续接已有申请，不新建。二段式配对依赖这条路。
        if (options.ResumeRequestId is not null || options.ResumeUserCode is not null)
        {
            if (!Guid.TryParse(options.ResumeRequestId, out var resumeId))
                return Usage("--request 必须是配对时返回的 UUID。");
            if (string.IsNullOrWhiteSpace(options.ResumeUserCode))
                return Usage("--code 必须是配对时输出的用户码。");

            var resumeDiscovery = await EntryDiscovery.DiscoverAsync(profile, options, cancellationToken);
            if (resumeDiscovery.Entry is null)
            {
                error.WriteLine($"无法确定自动化入口：{resumeDiscovery.Reason}");
                return ExitCodes.NetworkOrPin;
            }

            var (resumeKey, _) = CliStorage.LoadCredential(profile.Name);
            if (resumeKey is null)
            {
                error.WriteLine("未找到配对时的设备私钥；--request/--code 续接必须使用发起配对的同一台机器与同一 profile。");
                return ExitCodes.Unauthorized;
            }

            using var resumeClient = new ApiClient($"https://{resumeDiscovery.Entry}", profile.SpkiSha256, acceptAny: profile.SpkiSha256 is null)
            {
                AutomationInstanceId = profile.AutomationInstanceId
            };
            using var resumeDevice = DeviceKey.FromPrivateKey(resumeKey);
            return await WaitForApprovalAsync(resumeClient, resumeId, options.ResumeUserCode, resumeDevice, profile, profile.AutomationInstanceId, profile.SpkiSha256, configuration, cancellationToken);
        }

        var discovery = await EntryDiscovery.DiscoverAsync(profile, options, cancellationToken);
        if (discovery.Entry is null)
        {
            error.WriteLine($"无法确定自动化入口：{discovery.Reason}");
            return ExitCodes.NetworkOrPin;
        }

        var spki = options.Spki ?? profile.SpkiSha256;
        using var client = new ApiClient($"https://{discovery.Entry}", spki, acceptAny: spki is null || options.AcceptSpki);

        // 版本判定：连得上才可能得到 9；连不上只能是 8。
        ApiClient.ApiResult info;
        try { info = await client.GetInfoAsync(cancellationToken); }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            error.WriteLine($"无法连接到自动化端点 https://{discovery.Entry}。可能原因：服务端未启用 automation、服务端版本较旧无该端口，或该入口不通。");
            if (options.Verbose) error.WriteLine(exception.Message);
            return ExitCodes.NetworkOrPin;
        }

        if (!info.Ok)
        {
            error.WriteLine($"自动化端点返回错误：{info.Error?.Message}");
            return MapError(info);
        }

        // 版本协商：连得上但 apiVersions 不含本 CLI 支持的版本 → 退出码 9。
        // 这是退出码 9 唯一的触发点；连不上永远是 8。
        var apiVersions = info.Data?.TryGetProperty("apiVersions", out var versions) == true
            ? versions.EnumerateArray().Select(item => item.GetInt32()).ToArray()
            : [];
        if (apiVersions.Length > 0 && !apiVersions.Contains(ApiClient.SupportedApiVersion))
        {
            error.WriteLine($"服务端支持的 API 版本为 [{string.Join(',', apiVersions)}]，本 CLI 需要 {ApiClient.SupportedApiVersion}。请升级 CLI 或服务端。");
            return ExitCodes.VersionIncompatible;
        }

        var instanceId = info.Data?.TryGetProperty("automationInstanceId", out var instance) == true ? instance.GetString() : null;

        var device = DeviceKey.Generate();
        var scopes = options.Scopes.Count > 0 ? options.Scopes.ToArray() : ["server.read"];
        var subject = options.Subject ?? Environment.MachineName;

        var created = await client.CreatePairingAsync(subject, PlatformName(), CliVersion, scopes, device.PublicKeyJwk, cancellationToken);
        if (!created.Ok) return MapError(created);

        // 设备私钥必须在发起配对后立即落盘：二段式配对（CLI 退出 → 管理员批准 → --request 续接）
        // 依赖本地保存的私钥；若只在换令牌成功后保存，续接路径必然失败。
        // 此时 token 为空，凭据文件里的令牌字段留空，成功换取后再覆盖。
        if (!CliStorage.TrySaveCredential(profile.Name, device.PrivateKeyPkcs8Base64, token: string.Empty, out var keyWarning))
        {
            error.WriteLine(keyWarning);
            return ExitCodes.AuditOrStorage;
        }

        // 本次生效的 SPKI 必须在第一阶段就写回 profile：二段式配对的第一阶段在此返回，
        // 续接路径只认 profile.SpkiSha256；不写回会让证书固定在续接阶段退化为 acceptAny。
        if (spki != profile.SpkiSha256)
        {
            profile = profile with { SpkiSha256 = spki };
            ReplaceProfile(configuration, profile);
            CliStorage.SaveConfiguration(configuration);
        }

        var requestIdText = created.Data?.GetProperty("requestId").GetString();
        // 用户码必须出现在输出中：AI agent 无 TTY，需要读出它转达给管理员核对。
        var userCode = created.Data?.GetProperty("userCode").GetString() ?? string.Empty;
        output.WriteLine($"USER_CODE={userCode}");
        output.WriteLine($"配对请求已创建：{requestIdText}");
        output.WriteLine($"请在服务端管理页面的“自动化授权”面板核对用户码 {userCode} 后批准。");

        if (!options.Wait || !Guid.TryParse(requestIdText, out var requestId))
        {
            output.WriteLine("批准后执行：relaylink pair --wait --request " + requestIdText + " --code " + userCode);
            output.WriteLine("（--wait 需要 --request 与 --code 才能续接原申请；不带它们会创建新申请。）");
            return ExitCodes.ApprovalRequired;
        }

        return await WaitForApprovalAsync(client, requestId, userCode, device, profile, instanceId, spki, configuration, cancellationToken);
    }

    /// <summary>
    /// 续接等待已创建的配对申请。二段式配对的关键：--wait 必须能续接原 requestId，
    /// 否则会新建第二个申请、要求管理员再批一次。
    /// </summary>
    private async Task<int> WaitForApprovalAsync(ApiClient client, Guid requestId, string userCode,
        DeviceKey device, CliProfile profile, string? instanceId, string? effectiveSpki, CliConfiguration configuration, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(11);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
            var status = await client.QueryPairingAsync(requestId, userCode, device, cancellationToken);
            if (status.Ok && status.Data?.TryGetProperty("state", out var state) == true && state.GetString() == "approved") break;
            if (!status.Ok && status.Error?.Code is "pairing_denied" or "pairing_expired" or "pairing_locked") return MapError(status);
        }

        var exchange = await client.ExchangeTokenAsync(requestId, userCode, device, cancellationToken);
        if (!exchange.Ok) return MapError(exchange);

        var token = exchange.Data?.GetProperty("token").GetString() ?? string.Empty;
        // 续接进程没有 /info 返回的 instanceId；换令牌响应里带 automationInstanceId，
        // 必须在这里兜底写入 profile，否则后续所有令牌请求的证明都会缺 instanceId。
        var effectiveInstanceId = instanceId;
        if (string.IsNullOrEmpty(effectiveInstanceId) &&
            exchange.Data?.TryGetProperty("automationInstanceId", out var exchangeInstance) == true)
            effectiveInstanceId = exchangeInstance.GetString();

        var saved = CliStorage.TrySaveCredential(profile.Name, device.PrivateKeyPkcs8Base64, token, out var warning);
        if (!saved)
        {
            error.WriteLine(warning);
            return ExitCodes.AuditOrStorage;
        }

        // 本次实际生效的 SPKI 必须写回 profile：否则用户显式给的 --spki 在第一次
        // 配对成功后就丢失，后续连接退化为 acceptAny，与「配对后固定」承诺冲突。
        profile = profile with { AutomationInstanceId = effectiveInstanceId, SpkiSha256 = effectiveSpki ?? profile.SpkiSha256 };
        ReplaceProfile(configuration, profile);
        CliStorage.SaveConfiguration(configuration);

        if (options.Json)
            output.WriteLine(JsonSerializer.Serialize(new { schemaVersion = 1, data = new { grantId = exchange.Data?.GetProperty("grantId").GetString(), requestId = requestId.ToString() } }, Json));
        else
            output.WriteLine($"配对完成，令牌已保存。{warning}");
        return ExitCodes.Success;
    }

    /// <summary>查询配对状态：需要 --request 与发起配对的同一 profile。</summary>
    private async Task<int> PairStatusAsync(CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(options.ResumeRequestId, out var requestId))
            return Usage("用法：relaylink pair status --request <uuid>（配对时返回的 ID）。");

        var (profile, client, device) = await OpenPairingSessionAsync(cancellationToken);
        if (client is null || device is null) return LastErrorCode;

        var status = await client.QueryPairingAsync(requestId, options.ResumeUserCode ?? string.Empty, device, cancellationToken);
        if (!status.Ok) return MapError(status);
        Emit(status.Data);
        return ExitCodes.Success;
    }

    /// <summary>取消自己的待批准配对请求。</summary>
    private async Task<int> PairCancelAsync(CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(options.ResumeRequestId, out var requestId))
            return Usage("用法：relaylink pair cancel --request <uuid>。");

        var (profile, client, device) = await OpenPairingSessionAsync(cancellationToken);
        if (client is null || device is null) return LastErrorCode;

        var result = await client.CancelPairingAsync(requestId, device, cancellationToken);
        if (!result.Ok) return MapError(result);
        Emit(result.Data);
        return ExitCodes.Success;
    }

    /// <summary>打开配对续接所需的会话（入口发现 + 设备私钥）。</summary>
    private async Task<(CliProfile? Profile, ApiClient? Client, DeviceKey? Device)> OpenPairingSessionAsync(CancellationToken cancellationToken)
    {
        var configuration = CliStorage.LoadConfiguration();
        var profile = ResolveProfile(configuration, out var profileError);
        if (profile is null) { LastErrorCode = profileError; return (null, null, null); }

        var (devicePrivateKey, _) = CliStorage.LoadCredential(profile.Name);
        if (devicePrivateKey is null)
        {
            error.WriteLine("未找到设备私钥；配对续接必须使用发起配对的同一台机器与同一 profile。");
            LastErrorCode = ExitCodes.Unauthorized;
            return (null, null, null);
        }

        var discovery = await EntryDiscovery.DiscoverAsync(profile, options, cancellationToken);
        if (discovery.Entry is null)
        {
            error.WriteLine($"无法确定自动化入口：{discovery.Reason}");
            LastErrorCode = ExitCodes.NetworkOrPin;
            return (null, null, null);
        }

        var client = new ApiClient($"https://{discovery.Entry}", profile.SpkiSha256, acceptAny: profile.SpkiSha256 is null);
        return (profile, client, DeviceKey.FromPrivateKey(devicePrivateKey));
    }

    // ---------------- 认证自检 ----------------

    private async Task<int> AuthAsync(string[] rest, CancellationToken cancellationToken)
    {
        var sub = rest.FirstOrDefault() ?? "whoami";
        var configuration = CliStorage.LoadConfiguration();
        var profile = ResolveProfile(configuration, out var profileError);
        if (profile is null) return profileError;

        if (sub is "profiles")
        {
            foreach (var item in configuration.Profiles)
                output.WriteLine($"{item.Name}\t{item.Entry ?? "(自动发现)"}\t{item.AutomationInstanceId ?? "(未配对)"}");
            return ExitCodes.Success;
        }

        if (sub is "logout")
        {
            var storage = Path.Combine(CliStorage.CredentialDirectory, profile.Name + ".json");
            if (File.Exists(storage)) File.Delete(storage);
            output.WriteLine("已删除本地令牌。服务端授权仍有效，需在管理页面撤销。");
            return ExitCodes.Success;
        }

        var (devicePrivateKey, token) = CliStorage.LoadCredential(profile.Name);
        if (devicePrivateKey is null || string.IsNullOrEmpty(token))
        {
            error.WriteLine("尚未完成配对（设备私钥已保存但令牌未换取）。请完成 pair --wait --request/--code，或重新 pair。");
            return ExitCodes.Unauthorized;
        }

        using var device = DeviceKey.FromPrivateKey(devicePrivateKey);
        var discovery = await EntryDiscovery.DiscoverAsync(profile, options, cancellationToken);
        if (discovery.Entry is null) return NetworkError(discovery.Reason);

        using var client = new ApiClient($"https://{discovery.Entry}", profile.SpkiSha256, acceptAny: profile.SpkiSha256 is null)
        {
            AutomationInstanceId = profile.AutomationInstanceId
        };
        var result = await client.WhoAmIAsync(device, token, cancellationToken);
        if (!result.Ok) return MapError(result);

        Emit(new { subject = result.Data?.GetProperty("subject").GetString(), scopes = result.Data?.GetProperty("scopes"), expiresAtUtc = result.Data?.GetProperty("expiresAtUtc").GetString() });
        return ExitCodes.Success;
    }

    private async Task<int> DoctorAsync(CancellationToken cancellationToken)
    {
        var configuration = CliStorage.LoadConfiguration();
        output.WriteLine($"配置文件：{CliStorage.ConfigurationPath}{(File.Exists(CliStorage.ConfigurationPath) ? "" : "（不存在）")}");
        output.WriteLine($"凭据目录：{CliStorage.CredentialDirectory}");
        output.WriteLine($"profile 数量：{configuration.Profiles.Count}");
        foreach (var profile in configuration.Profiles)
        {
            var (_, token) = CliStorage.LoadCredential(profile.Name);
            var discovery = await EntryDiscovery.DiscoverAsync(profile, options, cancellationToken);
            output.WriteLine($"  {profile.Name}: 入口={discovery.Entry ?? discovery.Reason ?? "(未发现)"}  令牌={(string.IsNullOrEmpty(token) ? "未换取（配对未完成）" : "已保存")}");
        }

        return ExitCodes.Success;
    }

    // ---------------- 资源命令 ----------------

    private async Task<int> ServerAsync(string[] rest, CancellationToken cancellationToken)
    {
        var sub = rest.FirstOrDefault() ?? "health";
        var session = await OpenSessionAsync(cancellationToken);
        if (session is null) return LastErrorCode;

        var result = sub switch
        {
            "health" => await session.Client.GetOverviewAsync(session.Device, session.Token, cancellationToken),
            "overview" => await session.Client.GetOverviewAsync(session.Device, session.Token, cancellationToken),
            "version" => await session.Client.GetVersionAsync(session.Device, session.Token, cancellationToken),
            _ => null
        };

        if (result is null) return Usage($"未知的 server 子命令：{sub}");
        if (!result.Ok) return MapError(result);
        Emit(result.Data);
        return ExitCodes.Success;
    }

    private async Task<int> ClientsAsync(string[] rest, CancellationToken cancellationToken)
    {
        var session = await OpenSessionAsync(cancellationToken);
        if (session is null) return LastErrorCode;
        var result = await session.Client.ListClientsAsync(session.Device, session.Token, cancellationToken);
        if (!result.Ok) return MapError(result);
        Emit(result.Data);
        return ExitCodes.Success;
    }

    /// <summary>
    /// 子命令参数解析：从 rest（已排除全局选项）按「--name value」对提取。
    /// 布尔旗标值记为 "true"。这是子命令专用参数的唯一解析入口。
    /// </summary>
    private static Dictionary<string, string> ParseSubArguments(string[] rest)
    {
        var parsed = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < rest.Length; index++)
        {
            var token = rest[index];
            if (!token.StartsWith('-') || token.Length <= 1) continue;
            var name = token.TrimStart('-');
            if (index + 1 < rest.Length && !rest[index + 1].StartsWith('-'))
            {
                parsed[name] = rest[index + 1];
                index++;
            }
            else
            {
                parsed[name] = "true";
            }
        }

        return parsed;
    }

    private async Task<int> ChannelsAsync(string[] rest, CancellationToken cancellationToken)
    {
        var sub = rest.FirstOrDefault() ?? "ls";
        var arguments = ParseSubArguments(rest.Skip(1).ToArray());
        var clientId = arguments.TryGetValue("target", out var targetClient) ? targetClient
            : rest.Skip(1).FirstOrDefault(argument => !argument.StartsWith('-')) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(clientId)) return Usage("需要 clientId。");

        var session = await OpenSessionAsync(cancellationToken);
        if (session is null) return LastErrorCode;

        if (sub is "ls")
        {
            var result = await session.Client.ListChannelsAsync(clientId, session.Device, session.Token, cancellationToken);
            if (!result.Ok) return MapError(result);
            Emit(result.Data);
            return ExitCodes.Success;
        }

        if (sub is "set")
        {
            if (!arguments.TryGetValue("channel", out var channelId)) return Usage("需要 --channel。");
            if (options.DryRun)
            {
                output.WriteLine($"[dry-run] 将 PATCH {clientId}/{channelId}：displayName={arguments.GetValueOrDefault("display-name")}, tags={arguments.GetValueOrDefault("tags")}");
                return ExitCodes.Success;
            }

            var patch = new Dictionary<string, object?>();
            if (arguments.TryGetValue("display-name", out var displayName)) patch["displayName"] = displayName;
            if (arguments.TryGetValue("tags", out var tags)) patch["tags"] = tags.Split(',');
            var key = Guid.NewGuid().ToString();
            var result = await session.Client.PatchChannelAsync(clientId, channelId, session.Device, session.Token, patch, key, cancellationToken);
            if (!result.Ok) return MapError(result);
            Emit(result.Data);
            return ExitCodes.Success;
        }

        return Usage($"未知的 channels 子命令：{sub}");
    }

    private async Task<int> MappingsAsync(string[] rest, CancellationToken cancellationToken)
    {
        var arguments = ParseSubArguments(rest.Skip(1).ToArray());
        var clientId = arguments.TryGetValue("target", out var targetClient) ? targetClient
            : rest.Skip(1).FirstOrDefault(argument => !argument.StartsWith('-')) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(clientId)) return Usage("需要 clientId。");
        var session = await OpenSessionAsync(cancellationToken);
        if (session is null) return LastErrorCode;
        var result = await session.Client.ListMappingsAsync(clientId, session.Device, session.Token, cancellationToken);
        if (!result.Ok) return MapError(result);
        Emit(result.Data);
        return ExitCodes.Success;
    }

    private async Task<int> ChangesAsync(string[] rest, CancellationToken cancellationToken)
    {
        var sub = rest.FirstOrDefault() ?? "ls";
        var session = await OpenSessionAsync(cancellationToken);
        if (session is null) return LastErrorCode;

        switch (sub)
        {
            case "ls":
            {
                var result = await session.Client.ListChangesAsync(session.Device, session.Token, cancellationToken);
                if (!result.Ok) return MapError(result);
                Emit(result.Data);
                return ExitCodes.Success;
            }
            case "get":
            {
                if (!Guid.TryParse(rest.Skip(1).FirstOrDefault(), out var id)) return Usage("需要申请 ID。");
                var result = await session.Client.GetChangeAsync(id, session.Device, session.Token, cancellationToken);
                if (!result.Ok) return MapError(result);
                Emit(result.Data);
                return ExitCodes.Success;
            }
            case "cancel":
            {
                if (!Guid.TryParse(rest.Skip(1).FirstOrDefault(), out var id)) return Usage("需要申请 ID。");
                var result = await session.Client.CancelChangeAsync(id, session.Device, session.Token, cancellationToken);
                if (!result.Ok) return MapError(result);
                Emit(result.Data);
                return ExitCodes.Success;
            }
            case "submit":
            {
                var arguments = ParseSubArguments(rest.Skip(1).ToArray());
                if (!arguments.TryGetValue("type", out var changeType)) return Usage("需要 --type。");
                if (!arguments.TryGetValue("target", out var changeTarget)) return Usage("需要 --target <clientId>。");
                if (!arguments.TryGetValue("payload", out var payloadPath) || !File.Exists(payloadPath))
                    return Usage("需要 --payload <可读的 JSON 文件>。");
                arguments.TryGetValue("channel", out var changeChannel);
                arguments.TryGetValue("reason", out var changeReason);

                string payloadText;
                try { payloadText = File.ReadAllText(payloadPath); }
                catch (IOException exception) { error.WriteLine($"无法读取 payload：{exception.Message}"); return ExitCodes.Usage; }

                JsonDocument payload;
                try { payload = JsonDocument.Parse(payloadText); }
                catch (JsonException) { error.WriteLine("payload 不是有效 JSON。"); return ExitCodes.Usage; }

                using (payload)
                {
                    if (options.DryRun)
                    {
                        output.WriteLine($"[dry-run] 将提交 {changeType} 申请，target={changeTarget}，payload={payload.RootElement.GetRawText()}");
                        output.WriteLine("[dry-run] 未提交任何变更。");
                        return ExitCodes.Success;
                    }

                    var target = new Dictionary<string, object?> { ["clientId"] = changeTarget };
                    if (changeChannel is not null) target["channelId"] = changeChannel;
                    else if ((changeType is "channel.create" or "channel.delete" or "channel.security" or "mapping.create") &&
                             payload.RootElement.ValueKind == JsonValueKind.Object &&
                             payload.RootElement.TryGetProperty("channelId", out var payloadChannel))
                        // 服务端 BuildTargetKey 要求 target 含 clientId + channelId。
                        // SKILL.md 的 C 类示例只给 --target，这里从 payload 补齐，不改用户输入。
                        target["channelId"] = payloadChannel.GetString();
                    if (changeType == "mapping.create" &&
                        payload.RootElement.ValueKind == JsonValueKind.Object &&
                        payload.RootElement.TryGetProperty("mappingId", out var payloadMapping))
                        target["mappingId"] = payloadMapping.GetString();
                    var key = Guid.NewGuid().ToString();
                    var result = await session.Client.SubmitChangeAsync(session.Device, session.Token, new
                    {
                        type = changeType,
                        target,
                        payload = payload.RootElement,
                        reason = changeReason
                    }, key, cancellationToken);

                    if (!result.Ok) return MapError(result);
                    if (!options.Json)
                        output.WriteLine("变更申请已提交，等待管理员在管理页面批准。批准前不会产生任何配置变更。");
                    Emit(result.Data);
                    return ExitCodes.Success;
                }
            }
            default:
                return Usage($"未知的 changes 子命令：{sub}");
        }
    }

    private async Task<int> SkillAsync(string[] rest, CancellationToken cancellationToken)
    {
        if (rest.FirstOrDefault() != "install") return Usage("用法：relaylink skill install --host <host> [--force]");
        var host = rest.Skip(1).FirstOrDefault(argument => !argument.StartsWith('-')) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(host)) return Usage("需要 --host <host>。");

        output.WriteLine($"skill 安装目标宿主：{host}");
        output.WriteLine("请把随发行物提供的 skills/relaylink-admin 目录复制到该宿主的 skill 目录。");
        output.WriteLine("前置要求：目标宿主需存在 Node.js 运行时（包装脚本依赖它）。");
        await Task.CompletedTask;
        return ExitCodes.Success;
    }

    // ---------------- 会话与错误 ----------------

    private sealed record Session(ApiClient Client, DeviceKey Device, string Token) : IDisposable
    {
        public void Dispose()
        {
            Client.Dispose();
            Device.Dispose();
        }
    }

    private int LastErrorCode { get; set; }

    private async Task<Session?> OpenSessionAsync(CancellationToken cancellationToken)
    {
        var configuration = CliStorage.LoadConfiguration();
        var profile = ResolveProfile(configuration, out var profileError);
        if (profile is null) { LastErrorCode = profileError; return null; }

        var (devicePrivateKey, token) = CliStorage.LoadCredential(profile.Name);
        if (devicePrivateKey is null || string.IsNullOrEmpty(token))
        {
            error.WriteLine("尚未完成配对（设备私钥已保存但令牌未换取）。请完成 pair --wait --request/--code，或重新 pair。");
            LastErrorCode = ExitCodes.Unauthorized;
            return null;
        }

        var discovery = await EntryDiscovery.DiscoverAsync(profile, options, cancellationToken);
        if (discovery.Entry is null)
        {
            error.WriteLine($"无法确定入口：{discovery.Reason}");
            LastErrorCode = ExitCodes.NetworkOrPin;
            return null;
        }

        var client = new ApiClient($"https://{discovery.Entry}", profile.SpkiSha256, acceptAny: profile.SpkiSha256 is null)
        {
            AutomationInstanceId = profile.AutomationInstanceId
        };
        return new Session(client, DeviceKey.FromPrivateKey(devicePrivateKey), token);
    }

    private CliProfile? ResolveProfile(CliConfiguration configuration, out int errorCode)
    {
        errorCode = ExitCodes.Usage;
        if (configuration.Profiles.Count == 0)
        {
            // 允许用 --entry 直接试连，但配对仍需要一个 profile 名称。
            configuration.Profiles.Add(new CliProfile(options.Profile ?? "default", options.Entry, CliProfile.DefaultDashboardPort, null, null, null));
        }

        CliProfile? profile;
        if (options.Profile is not null)
            profile = configuration.Profiles.SingleOrDefault(item => item.Name == options.Profile);
        else if (configuration.Profiles.Count == 1)
            profile = configuration.Profiles[0];
        else
        {
            error.WriteLine("存在多个 profile，请用 --profile 显式指定，不按第一个猜测。");
            errorCode = ExitCodes.Usage;
            return null;
        }

        if (profile is null)
        {
            error.WriteLine($"找不到 profile：{options.Profile}");
            errorCode = ExitCodes.Usage;
        }

        return profile;
    }

    private static void ReplaceProfile(CliConfiguration configuration, CliProfile profile)
    {
        var index = configuration.Profiles.FindIndex(item => item.Name == profile.Name);
        if (index >= 0) configuration.Profiles[index] = profile;
        else configuration.Profiles.Add(profile);
    }

    private int NetworkError(string? reason)
    {
        error.WriteLine($"无法确定入口：{reason}");
        return ExitCodes.NetworkOrPin;
    }

    /// <summary>
    /// 把服务端错误码映射为退出码。这条映射是 skill 与 agent 的判断依据。
    /// --json 模式下错误必须输出结构化 JSON（commands.md 的承诺）：skill 按退出码
    /// 与 error.code 判断下一步，纯文本 stderr 无法被 agent 解析。
    /// </summary>
    private int MapError(ApiClient.ApiResult result)
    {
        var code = result.Error?.Code ?? "unknown";
        var exit = code switch
        {
            "unauthenticated" or "token_expired" or "token_revoked" or "proof_invalid" or "proof_replayed" => ExitCodes.Unauthorized,
            "scope_insufficient" => ExitCodes.ScopeInsufficient,
            "pairing_denied" or "pairing_expired" or "pairing_locked" or "change_request_not_approved" => ExitCodes.ApprovalRequired,
            "audit_unavailable" or "storage_unavailable" => ExitCodes.AuditOrStorage,
            "unsupported_api_version" => ExitCodes.VersionIncompatible,
            "rate_limited" or "pending_capacity_exceeded" => ExitCodes.ServerRejected,
            "validation_failed" or "unknown_field" or "not_found" or "conflict" or "requires_change_request"
                or "pairing_not_found" or "pairing_already_completed" or "automation_disabled" => ExitCodes.ServerRejected,
            _ => ExitCodes.ServerRejected
        };

        var hint = exit == ExitCodes.ScopeInsufficient
            ? "当前令牌缺少所需 scope。请在服务端重新配对并申请该 scope，不要改写参数重试。"
            : null;

        if (options.Json)
        {
            // 失败输出与 commands.md 的承诺一致：{"schemaVersion":1,"error":{...}}。
            output.WriteLine(JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                error = new
                {
                    code,
                    message = result.Error?.Message ?? "未知错误。",
                    retryable = result.Error?.Retryable ?? false,
                    requestId = result.Error?.RequestId
                }
            }, Json));
        }
        else
        {
            error.WriteLine($"{code}: {result.Error?.Message ?? "未知错误。"}");
            if (hint is not null) error.WriteLine(hint);
        }

        if (exit == ExitCodes.ScopeInsufficient && hint is not null && options.Json)
            error.WriteLine(hint);

        LastErrorCode = exit;
        return exit;
    }

    private void Emit(object? data)
    {
        if (data is JsonElement element)
        {
            if (options.Json)
                output.WriteLine(JsonSerializer.Serialize(new { schemaVersion = 1, data = element }, Json));
            else
                output.WriteLine(element.GetRawText());
            return;
        }

        var text = JsonSerializer.Serialize(data, Json);
        output.WriteLine(options.Json ? JsonSerializer.Serialize(new { schemaVersion = 1, data = data }, Json) : text);
    }

    private static string PlatformName() =>
        OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";

    private static string CliVersion =>
        typeof(CliApp).Assembly.GetName().Version?.ToString() ?? "0.1.0";
}