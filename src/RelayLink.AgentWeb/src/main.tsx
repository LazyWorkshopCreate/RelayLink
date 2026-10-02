import { useCallback, useEffect, useState } from "react";
import { createRoot } from "react-dom/client";
import {
    Activity,
    ArrowLeftRight,
    Check,
    CircleAlert,
    Cloud,
    LockKeyhole,
    Plus,
    Power,
    RefreshCw,
    Server,
    ShieldCheck,
    Settings2,
    Trash2,
    Upload,
    X,
} from "lucide-react";
import "./styles.css";

type Channel = {
    channelId: string;
    displayName: string;
    enabled: boolean;
    authorizedClientsOnly: boolean;
    endToEndEncryptionEnabled: boolean;
    targetHost: string;
    targetPort: number;
};
type Mapping = {
    mappingId: string;
    enabled: boolean;
    targetClientId: string;
    targetChannelId: string;
    localAddress: string | null;
};
type AgentServer = {
    profileId: string;
    enabled: boolean;
    serverHost: string;
    useTls: boolean;
    clientId: string;
    online: boolean;
    updatedAtUtc: string | null;
    pendingConnections: number;
    activeConnections: number;
    capacityRejected: number;
    channels: Channel[];
    mappings: Mapping[];
};
type Profile = {
    profileId: string;
    enabled?: boolean;
    serverHost: string;
    serverPort: number;
    dataPort: number;
    clientId: string;
    secret: string;
    useTls: boolean;
    trustedCaPemBase64: string | null;
    maxConnections: number;
    maxPendingConnections: number;
    reconnect: {
        initialDelaySeconds: number;
        maxDelaySeconds: number;
        permanentErrorDelaySeconds: number;
    };
};
type Session = { csrfToken: string; version: string };

async function request<T>(path: string, init?: RequestInit): Promise<T> {
    const response = await fetch(path, {
        cache: "no-store",
        credentials: "same-origin",
        ...init,
    });
    if (!response.ok) {
        const body = await response.json().catch(() => ({}));
        throw new Error(
            body.message || body.detail || `请求失败 (${response.status})`,
        );
    }
    return response.status === 204
        ? (undefined as T)
        : (response.json() as Promise<T>);
}

function App() {
    const [servers, setServers] = useState<AgentServer[]>([]);
    const [session, setSession] = useState<Session | null>(null);
    const [lastRefresh, setLastRefresh] = useState<Date | null>(null);
    const [error, setError] = useState("");
    const [notice, setNotice] = useState("");
    const [editorId, setEditorId] = useState<string | null | undefined>(
        undefined,
    );
    const [removeId, setRemoveId] = useState<string | null>(null);
    const [disableId, setDisableId] = useState<string | null>(null);
    const [profile, setProfile] = useState<Profile | null>(null);
    const [fileName, setFileName] = useState("");
    const [confirmExistingState, setConfirmExistingState] = useState(false);
    const [busy, setBusy] = useState(false);

    const refresh = useCallback(async () => {
        try {
            const status = await request<{ servers: AgentServer[] }>(
                "/api/v2/status",
            );
            setServers(status.servers);
            setLastRefresh(new Date());
        } catch (cause) {
            setError(
                cause instanceof Error
                    ? cause.message
                    : "无法读取 Agent 状态。",
            );
        }
    }, []);

    useEffect(() => {
        void refresh();
        void request<Session>("/api/v2/admin/session")
            .then(setSession)
            .catch(() => setError("无法取得本机管理会话。"));
        const timer = window.setInterval(() => void refresh(), 5000);
        return () => window.clearInterval(timer);
    }, [refresh]);

    function openEditor(profileId: string | null) {
        setEditorId(profileId);
        setProfile(null);
        setFileName("");
        setConfirmExistingState(false);
        setError("");
    }

    function closeEditor() {
        setProfile(null);
        setFileName("");
        setEditorId(undefined);
    }

    async function importJson(file?: File) {
        if (!file) return;
        try {
            const downloaded = JSON.parse(await file.text()) as {
                servers?: Profile[];
            };
            if (
                !Array.isArray(downloaded.servers) ||
                downloaded.servers.length !== 1
            )
                throw new Error("请选择只包含一个服务端的新版下载配置。");
            const server = downloaded.servers[0];
            if (
                !server ||
                typeof server !== "object" ||
                typeof server.clientId !== "string" ||
                typeof server.serverHost !== "string" ||
                typeof server.secret !== "string" ||
                !server.secret
            )
                throw new Error("配置文件缺少服务端连接信息。");
            let profileId: string;
            if (editorId === null) {
                const base = /^[a-z0-9][a-z0-9_-]{0,63}$/.test(server.profileId)
                    ? server.profileId
                    : "primary";
                profileId = base;
                for (
                    let suffix = 2;
                    servers.some((item) => item.profileId === profileId);
                    suffix++
                )
                    profileId = `${base.slice(0, 60)}-${suffix}`;
            } else {
                const existing = servers.find(
                    (item) => item.profileId === editorId,
                );
                if (!existing || existing.clientId !== server.clientId)
                    throw new Error(
                        "导入文件的客户端 ID 与所选服务端不同，不能复用原有身份。请在远端核对配置。",
                    );
                profileId = editorId!;
            }
            const existing = servers.find(
                (item) => item.profileId === editorId,
            );
            setProfile({
                ...server,
                profileId,
                enabled: existing?.enabled ?? true,
            });
            setFileName(file.name);
            setNotice("");
            setError("");
        } catch (cause) {
            setProfile(null);
            setFileName("");
            setError(
                cause instanceof Error ? cause.message : "配置文件无法读取。",
            );
        }
    }

    async function saveImport() {
        if (!session || !profile || editorId === undefined || busy) return;
        setBusy(true);
        setError("");
        try {
            const updating = editorId !== null;
            const result = await request<{ version: string }>(
                updating
                    ? `/api/v2/admin/servers/${encodeURIComponent(editorId)}`
                    : "/api/v2/admin/servers",
                {
                    method: updating ? "PUT" : "POST",
                    headers: {
                        "Content-Type": "application/json",
                        "X-RelayLink-CSRF": session.csrfToken,
                    },
                    body: JSON.stringify({
                        version: session.version,
                        server: profile,
                        ...(updating ? {} : { confirmExistingState }),
                    }),
                },
            );
            setSession({ ...session, version: result.version });
            closeEditor();
            setNotice(
                `${profile.profileId} 已${updating ? "更新" : "新增"}，Agent 将重新连接。`,
            );
            await refresh();
        } catch (cause) {
            setError(cause instanceof Error ? cause.message : "保存失败。");
        } finally {
            setProfile(null);
            setFileName("");
            setBusy(false);
        }
    }

    async function remove() {
        if (!session || !removeId || busy) return;
        setBusy(true);
        setError("");
        try {
            const result = await request<{ version: string }>(
                `/api/v2/admin/servers/${encodeURIComponent(removeId)}`,
                {
                    method: "DELETE",
                    headers: {
                        "Content-Type": "application/json",
                        "X-RelayLink-CSRF": session.csrfToken,
                    },
                    body: JSON.stringify({ version: session.version }),
                },
            );
            setSession({ ...session, version: result.version });
            setNotice(`${removeId} 已从本机移除。`);
            setRemoveId(null);
            await refresh();
        } catch (cause) {
            setError(cause instanceof Error ? cause.message : "删除失败。");
        } finally {
            setBusy(false);
        }
    }

    async function setEnabled(profileId: string, enabled: boolean) {
        if (!session || busy) return;
        setBusy(true);
        setError("");
        try {
            const result = await request<{ version: string }>(
                `/api/v2/admin/servers/${encodeURIComponent(profileId)}/enabled`,
                {
                    method: "PUT",
                    headers: {
                        "Content-Type": "application/json",
                        "X-RelayLink-CSRF": session.csrfToken,
                    },
                    body: JSON.stringify({ version: session.version, enabled }),
                },
            );
            setSession({ ...session, version: result.version });
            setDisableId(null);
            setNotice(
                `${profileId} 已${enabled ? "启用，正在重新连接" : "停用并断开连接"}。`,
            );
            await refresh();
        } catch (cause) {
            setError(cause instanceof Error ? cause.message : "状态切换失败。");
        } finally {
            setBusy(false);
        }
    }

    const online = servers.filter((server) => server.online).length;
    const channels = servers.reduce(
        (total, server) => total + server.channels.length,
        0,
    );
    const mappings = servers.reduce(
        (total, server) =>
            total +
            server.mappings.filter((mapping) => mapping.localAddress).length,
        0,
    );
    const active = servers.reduce(
        (total, server) => total + server.activeConnections,
        0,
    );

    return (
        <div className="shell agent-shell">
            <header className="topbar">
                <div className="brand">
                    <img
                        className="brand-mark"
                        src="/relaylink-icon.svg"
                        alt=""
                    />
                    <span>
                        RelayLink<small>AGENT CONTROL CENTER</small>
                    </span>
                </div>
                <div className="top-actions">
                    <span className="live-label">
                        <span className={`live-dot${error ? " stale" : ""}`} />
                        本机监控
                    </span>
                    <span className="auth-status">
                        <ShieldCheck size={16} /> 仅 127.0.0.1
                    </span>
                </div>
            </header>
            <main className="main">
                <section className="page-intro">
                    <div>
                        <div className="eyebrow">OVERVIEW / 总览</div>
                        <h1>Agent 连接概览</h1>
                        <p>查看各服务端的连接、通道和本机互访入口。</p>
                    </div>
                    <button
                        className="ghost refresh"
                        onClick={() => void refresh()}
                    >
                        <RefreshCw size={16} /> 刷新{" "}
                        <span>
                            {lastRefresh?.toLocaleTimeString("zh-CN") ?? "—"}
                        </span>
                    </button>
                </section>
                {error && (
                    <div className="agent-alert error" role="alert">
                        <CircleAlert size={17} />
                        <span>{error}</span>
                        <button
                            aria-label="关闭错误"
                            onClick={() => setError("")}
                        >
                            <X size={16} />
                        </button>
                    </div>
                )}
                {notice && (
                    <div className="agent-alert success" role="status">
                        <Check size={17} />
                        <span>{notice}</span>
                        <button
                            aria-label="关闭通知"
                            onClick={() => setNotice("")}
                        >
                            <X size={16} />
                        </button>
                    </div>
                )}
                <section className="agent-metrics" aria-label="Agent 概况">
                    <div className="metric">
                        <div className="metric-icon blue">
                            <Cloud size={20} />
                        </div>
                        <div>
                            <span>在线服务端</span>
                            <strong>
                                {online} / {servers.length}
                            </strong>
                            <small>已连接 / 已注册</small>
                        </div>
                    </div>
                    <div className="metric">
                        <div className="metric-icon violet">
                            <Server size={20} />
                        </div>
                        <div>
                            <span>被访问通道</span>
                            <strong>{channels}</strong>
                            <small>来自在线服务端</small>
                        </div>
                    </div>
                    <div className="metric">
                        <div className="metric-icon green">
                            <ArrowLeftRight size={20} />
                        </div>
                        <div>
                            <span>可用互访入口</span>
                            <strong>{mappings}</strong>
                            <small>已绑定本机地址</small>
                        </div>
                    </div>
                    <div className="metric">
                        <div className="metric-icon orange">
                            <Activity size={20} />
                        </div>
                        <div>
                            <span>活动连接</span>
                            <strong>{active}</strong>
                            <small>所有服务端合计</small>
                        </div>
                    </div>
                </section>
                <section className="section-head agent-section-head">
                    <div>
                        <div className="eyebrow">
                            SERVERS & CHANNELS / 服务端与通道
                        </div>
                        <h2>
                            服务端列表 <span>{servers.length}</span>
                        </h2>
                    </div>
                    <button
                        className="primary"
                        onClick={() => openEditor(null)}
                        disabled={!session}
                    >
                        <Plus size={17} /> 新增服务端
                    </button>
                </section>
                {servers.length === 0 && (
                    <div className="agent-empty">
                        <Cloud size={34} />
                        <h3>尚未配置服务端</h3>
                        <p>导入服务端下载的单服务端 Agent 配置。</p>
                        <button
                            className="primary"
                            disabled={!session}
                            onClick={() => openEditor(null)}
                        >
                            <Plus size={16} /> 新增服务端
                        </button>
                    </div>
                )}
                <div className="agent-server-list">
                    {servers.map((server) => (
                        <article
                            className="agent-server"
                            key={server.profileId}
                        >
                            <div className="agent-server-header">
                                <div className="agent-server-title">
                                    <div className="metric-icon blue">
                                        <Server size={20} />
                                    </div>
                                    <div>
                                        <h3>{server.profileId}</h3>
                                        <p>
                                            {server.serverHost} ·{" "}
                                            {server.useTls ? "TLS" : "明文控制"}{" "}
                                            · 客户端 ID {server.clientId}
                                        </p>
                                    </div>
                                </div>
                                <div className="agent-server-actions">
                                    <span
                                        className={`agent-badge ${!server.enabled ? "disabled" : server.online ? "online" : "offline"}`}
                                    >
                                        {!server.enabled
                                            ? "● 已停用"
                                            : server.online
                                              ? "● 在线"
                                              : "● 离线"}
                                    </span>
                                    <button
                                        className="ghost"
                                        disabled={!session || busy}
                                        onClick={() =>
                                            server.enabled
                                                ? setDisableId(server.profileId)
                                                : void setEnabled(
                                                      server.profileId,
                                                      true,
                                                  )
                                        }
                                    >
                                        <Power size={15} />{" "}
                                        {server.enabled ? "停用" : "启用"}
                                    </button>
                                    <button
                                        className="ghost"
                                        disabled={!session || busy}
                                        onClick={() =>
                                            openEditor(server.profileId)
                                        }
                                    >
                                        <Settings2 size={15} /> 导入修改
                                    </button>
                                    <button
                                        className="agent-delete"
                                        title={`删除 ${server.profileId}`}
                                        aria-label={`删除 ${server.profileId}`}
                                        disabled={!session || busy}
                                        onClick={() =>
                                            setRemoveId(server.profileId)
                                        }
                                    >
                                        <Trash2 size={16} />
                                    </button>
                                </div>
                            </div>
                            <div className="agent-server-stats">
                                <span>
                                    活动连接{" "}
                                    <strong>{server.activeConnections}</strong>
                                </span>
                                <span>
                                    待建立{" "}
                                    <strong>{server.pendingConnections}</strong>
                                </span>
                                <span>
                                    配额拒绝{" "}
                                    <strong>{server.capacityRejected}</strong>
                                </span>
                                <span>
                                    状态更新{" "}
                                    <strong>
                                        {server.updatedAtUtc
                                            ? new Date(
                                                  server.updatedAtUtc,
                                              ).toLocaleString("zh-CN")
                                            : "—"}
                                    </strong>
                                </span>
                            </div>
                            <div className="agent-panels">
                                <div className="agent-panel">
                                    <h4>
                                        被访问通道{" "}
                                        <span>{server.channels.length}</span>
                                    </h4>
                                    {server.channels.length ? (
                                        <div className="agent-table-scroll">
                                            <table>
                                                <thead>
                                                    <tr>
                                                        <th>通道</th>
                                                        <th>访问模式</th>
                                                        <th>目标</th>
                                                        <th>状态</th>
                                                    </tr>
                                                </thead>
                                                <tbody>
                                                    {server.channels.map(
                                                        (channel) => (
                                                            <tr
                                                                key={
                                                                    channel.channelId
                                                                }
                                                            >
                                                                <td>
                                                                    <b>
                                                                        {
                                                                            channel.displayName
                                                                        }
                                                                    </b>
                                                                    <small>
                                                                        {
                                                                            channel.channelId
                                                                        }
                                                                    </small>
                                                                </td>
                                                                <td>
                                                                    {channel.authorizedClientsOnly
                                                                        ? channel.endToEndEncryptionEnabled
                                                                            ? "授权互访 · 加密"
                                                                            : "授权互访 · 明文"
                                                                        : "普通代理"}
                                                                </td>
                                                                <td>
                                                                    <code>
                                                                        {
                                                                            channel.targetHost
                                                                        }
                                                                        :
                                                                        {
                                                                            channel.targetPort
                                                                        }
                                                                    </code>
                                                                </td>
                                                                <td>
                                                                    {channel.enabled
                                                                        ? "启用"
                                                                        : "禁用"}
                                                                </td>
                                                            </tr>
                                                        ),
                                                    )}
                                                </tbody>
                                            </table>
                                        </div>
                                    ) : (
                                        <p className="agent-panel-empty">
                                            {server.enabled
                                                ? "当前无在线通道"
                                                : "服务端已停用"}
                                        </p>
                                    )}
                                </div>
                                <div className="agent-panel">
                                    <h4>
                                        本机互访入口{" "}
                                        <span>{server.mappings.length}</span>
                                    </h4>
                                    {server.mappings.length ? (
                                        <div className="agent-table-scroll">
                                            <table>
                                                <thead>
                                                    <tr>
                                                        <th>入口</th>
                                                        <th>访问目标</th>
                                                        <th>本机地址</th>
                                                    </tr>
                                                </thead>
                                                <tbody>
                                                    {server.mappings.map(
                                                        (mapping) => (
                                                            <tr
                                                                key={
                                                                    mapping.mappingId
                                                                }
                                                            >
                                                                <td>
                                                                    <b>
                                                                        {
                                                                            mapping.mappingId
                                                                        }
                                                                    </b>
                                                                </td>
                                                                <td>
                                                                    {
                                                                        mapping.targetClientId
                                                                    }{" "}
                                                                    /{" "}
                                                                    {
                                                                        mapping.targetChannelId
                                                                    }
                                                                </td>
                                                                <td>
                                                                    {mapping.localAddress ? (
                                                                        <code>
                                                                            {
                                                                                mapping.localAddress
                                                                            }
                                                                        </code>
                                                                    ) : (
                                                                        <span className="agent-unavailable">
                                                                            不可用
                                                                        </span>
                                                                    )}
                                                                </td>
                                                            </tr>
                                                        ),
                                                    )}
                                                </tbody>
                                            </table>
                                        </div>
                                    ) : (
                                        <p className="agent-panel-empty">
                                            {server.enabled
                                                ? "当前无在线入口"
                                                : "服务端已停用"}
                                        </p>
                                    )}
                                </div>
                            </div>
                        </article>
                    ))}
                </div>
                <p className="agent-footer">
                    <LockKeyhole size={14} />{" "}
                    本机页面不显示已保存的注册密钥。停用保留本机配置和身份；删除本机服务端不会撤销远端身份。
                </p>
            </main>
            {editorId !== undefined && (
                <div
                    className="agent-overlay"
                    onMouseDown={(event) => {
                        if (event.target === event.currentTarget && !busy)
                            closeEditor();
                    }}
                >
                    <div
                        className="agent-dialog"
                        role="dialog"
                        aria-modal="true"
                        aria-labelledby="import-title"
                    >
                        <div className="agent-dialog-head">
                            <div>
                                <h2 id="import-title">
                                    {editorId === null
                                        ? "新增服务端"
                                        : `修改 ${editorId}`}
                                </h2>
                                <p>
                                    仅支持导入服务端下载的单服务端 Agent JSON
                                    配置。
                                </p>
                            </div>
                            <button
                                className="agent-close"
                                aria-label="关闭"
                                onClick={closeEditor}
                                disabled={busy}
                            >
                                <X size={18} />
                            </button>
                        </div>
                        {error && (
                            <div className="agent-alert error" role="alert">
                                <CircleAlert size={17} />
                                <span>{error}</span>
                            </div>
                        )}
                        <label className="agent-import">
                            <Upload size={17} />{" "}
                            {profile ? "重新选择 JSON" : "选择 Agent 配置 JSON"}
                            <input
                                type="file"
                                accept=".json,application/json"
                                onChange={(event) => {
                                    void importJson(event.target.files?.[0]);
                                    event.target.value = "";
                                }}
                            />
                        </label>
                        {profile && (
                            <div className="agent-import-summary">
                                <div className="agent-import-file">
                                    <Check size={16} /> {fileName}
                                </div>
                                <dl>
                                    <div>
                                        <dt>本机 profileId</dt>
                                        <dd>{profile.profileId}</dd>
                                    </div>
                                    <div>
                                        <dt>客户端 ID</dt>
                                        <dd>{profile.clientId}</dd>
                                    </div>
                                    <div>
                                        <dt>服务端</dt>
                                        <dd>
                                            {profile.serverHost}:
                                            {profile.serverPort}
                                        </dd>
                                    </div>
                                    <div>
                                        <dt>数据端口</dt>
                                        <dd>{profile.dataPort}</dd>
                                    </div>
                                    <div>
                                        <dt>控制连接</dt>
                                        <dd>
                                            {profile.useTls ? "TLS" : "明文"}
                                        </dd>
                                    </div>
                                    <div>
                                        <dt>连接配额</dt>
                                        <dd>
                                            {profile.maxConnections} / 待建立{" "}
                                            {profile.maxPendingConnections}
                                        </dd>
                                    </div>
                                </dl>
                                <p>注册密钥与 CA 内容不会在页面中回显。</p>
                            </div>
                        )}
                        {editorId === null && profile && (
                            <label className="agent-checkbox agent-state-confirm">
                                <input
                                    type="checkbox"
                                    checked={confirmExistingState}
                                    onChange={(event) =>
                                        setConfirmExistingState(
                                            event.target.checked,
                                        )
                                    }
                                />{" "}
                                确认同名旧状态仍属于此服务端身份
                            </label>
                        )}
                        {editorId !== null && (
                            <p className="agent-edit-hint">
                                {servers.find(
                                    (item) => item.profileId === editorId,
                                )?.enabled
                                    ? "修改会重连此服务端并中断其现有连接；"
                                    : "修改后仍保持停用；"}
                                其他服务端继续运行。导入文件的客户端 ID
                                必须与当前身份一致。
                            </p>
                        )}
                        <div className="agent-dialog-actions">
                            <button
                                className="ghost"
                                onClick={closeEditor}
                                disabled={busy}
                            >
                                取消
                            </button>
                            <button
                                className="primary"
                                onClick={() => void saveImport()}
                                disabled={busy || !session || !profile}
                            >
                                {busy
                                    ? "正在保存…"
                                    : editorId === null
                                      ? "导入并连接"
                                      : "导入并修改"}
                            </button>
                        </div>
                    </div>
                </div>
            )}
            {removeId && (
                <div className="agent-overlay">
                    <div
                        className="agent-dialog agent-confirm"
                        role="alertdialog"
                        aria-modal="true"
                        aria-labelledby="remove-title"
                    >
                        <div className="agent-dialog-head">
                            <div>
                                <h2 id="remove-title">删除 {removeId}？</h2>
                                <p>
                                    本机连接和互访入口将立即中断。远端客户端记录与注册密钥仍需由远端管理员另行禁用或删除。
                                </p>
                            </div>
                        </div>
                        {error && (
                            <div className="agent-alert error" role="alert">
                                <CircleAlert size={17} />
                                <span>{error}</span>
                            </div>
                        )}
                        <div className="agent-dialog-actions">
                            <button
                                className="ghost"
                                onClick={() => setRemoveId(null)}
                                disabled={busy}
                            >
                                取消
                            </button>
                            <button
                                className="agent-danger"
                                onClick={() => void remove()}
                                disabled={busy}
                            >
                                {busy ? "正在删除…" : "确认删除"}
                            </button>
                        </div>
                    </div>
                </div>
            )}
            {disableId && (
                <div className="agent-overlay">
                    <div
                        className="agent-dialog agent-confirm"
                        role="alertdialog"
                        aria-modal="true"
                        aria-labelledby="disable-title"
                    >
                        <div className="agent-dialog-head">
                            <div>
                                <h2 id="disable-title">停用 {disableId}？</h2>
                                <p>
                                    将断开该服务端的控制连接、业务连接及本机互访入口，并停止重连。配置和身份保留，可随时重新启用。
                                </p>
                            </div>
                        </div>
                        {error && (
                            <div className="agent-alert error" role="alert">
                                <CircleAlert size={17} />
                                <span>{error}</span>
                            </div>
                        )}
                        <div className="agent-dialog-actions">
                            <button
                                className="ghost"
                                onClick={() => setDisableId(null)}
                                disabled={busy}
                            >
                                取消
                            </button>
                            <button
                                className="primary"
                                onClick={() =>
                                    void setEnabled(disableId, false)
                                }
                                disabled={busy}
                            >
                                {busy ? "正在停用…" : "确认停用"}
                            </button>
                        </div>
                    </div>
                </div>
            )}
        </div>
    );
}

createRoot(document.getElementById("root")!).render(<App />);
