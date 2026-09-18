import { useCallback, useEffect, useRef, useState, type FormEvent, type ReactNode } from 'react';
import { createRoot } from 'react-dom/client';
import * as Dialog from '@radix-ui/react-dialog';
import {
    Activity,
    ArrowDownToLine,
    ArrowLeftRight,
    Check,
    ChevronDown,
    CircleHelp,
    Cloud,
    LockKeyhole,
    LogOut,
    Plus,
    RefreshCw,
    Search,
    Settings2,
    ShieldCheck,
    X,
} from 'lucide-react';
import './styles.css';

type Session = { authenticated: boolean; csrfToken?: string };
type Overview = {
    statsSinceUtc: string;
    snapshotTimeUtc: string;
    clientsOnline: number;
    clientsTotal: number;
    channelsAvailable: number;
    channelsTotal: number;
    activeConnections: number;
    bytesToTarget: number;
    bytesToCaller: number;
    peerCiphertextToTarget: number;
    peerCiphertextToCaller: number;
};
type Client = {
    clientId: string;
    displayName: string;
    enabled: boolean;
    online: boolean;
    lastHeartbeatUtc?: string;
    maxConnections: number;
    maxPendingConnections: number;
};
type Channel = {
    channelId: string;
    displayName: string;
    enabled: boolean;
    authorizedClientsOnly?: boolean;
    e2eCertificateSha256?: string;
    available: boolean;
    listenAddress: string;
    listenPort: number;
    targetHost: string;
    targetPort: number;
    maxConnections: number;
    targetConnectTimeoutSeconds: number;
    pendingConnections: number;
    activeConnections: number;
    bytesToTarget: number;
    bytesToCaller: number;
    peerCiphertextToTarget: number;
    peerCiphertextToCaller: number;
    targetLastResult?: string;
};
type Mapping = {
    mappingId: string;
    enabled: boolean;
    localAddress: string | null;
    localPort: number | null;
    available: boolean;
    targetClientId: string;
    targetChannelId: string;
};
type HistorySample = { timestampUtc: string; bytesToTarget: number; bytesToCaller: number };
type ViewClient = Client & { channels: Channel[]; mappings: Mapping[] };
type ClientForm = {
    clientId: string;
    displayName: string;
    enabled: boolean;
    maxConnections: number;
    maxPendingConnections: number;
    agentServerHost: string;
    trustedCaPemPath: string;
};
type ChannelForm = {
    channelId: string;
    displayName: string;
    enabled: boolean;
    listenAddress: string;
    listenPort: number;
    targetHost: string;
    targetPort: number;
    maxConnections: number;
    targetConnectTimeoutSeconds: number;
    authorizedClientsOnly: boolean;
    e2eCertificateSha256: string;
};
type MappingForm = {
    mappingId: string;
    enabled: boolean;
    targetClientId: string;
    targetChannelId: string;
};

const emptyClient: ClientForm = {
    clientId: '',
    displayName: '',
    enabled: true,
    maxConnections: 100,
    maxPendingConnections: 100,
    agentServerHost: '',
    trustedCaPemPath: '',
};
const emptyChannel: ChannelForm = {
    channelId: '',
    displayName: '',
    enabled: true,
    listenAddress: '127.0.0.1',
    listenPort: 19000,
    targetHost: '127.0.0.1',
    targetPort: 19001,
    maxConnections: 100,
    targetConnectTimeoutSeconds: 10,
    authorizedClientsOnly: false,
    e2eCertificateSha256: '',
};
const emptyMapping: MappingForm = {
    mappingId: '',
    enabled: true,
    targetClientId: '',
    targetChannelId: '',
};
const fmt = (n: number) =>
    n < 1024
        ? `${n || 0} B`
        : n < 1048576
          ? `${(n / 1024).toFixed(1)} KiB`
          : `${(n / 1048576).toFixed(1)} MiB`;
const time = (s?: string) => (s ? new Date(s).toLocaleString('zh-CN') : '—');
const enc = encodeURIComponent;

async function api<T>(path: string, init?: RequestInit): Promise<T> {
    const response = await fetch(path, { credentials: 'same-origin', cache: 'no-store', ...init });
    if (!response.ok) {
        const body = await response.json().catch(() => ({}));
        throw new Error(body.error || `${response.status} ${response.statusText}`);
    }
    return response.status === 204 ? (undefined as T) : (response.json() as Promise<T>);
}

function Modal({
    title,
    description,
    open,
    onClose,
    error,
    children,
}: {
    title: string;
    description?: string;
    open: boolean;
    onClose: () => void;
    error?: string;
    children: ReactNode;
}) {
    return (
        <Dialog.Root
            open={open}
            onOpenChange={(value) => {
                if (!value) onClose();
            }}
        >
            <Dialog.Portal>
                <Dialog.Overlay className="modal-overlay" />
                <Dialog.Content className="modal">
                    <div className="modal-heading">
                        <div>
                            <Dialog.Title>{title}</Dialog.Title>
                            {description && <Dialog.Description>{description}</Dialog.Description>}
                        </div>
                        <Dialog.Close className="icon-button" aria-label="关闭">
                            <X size={18} />
                        </Dialog.Close>
                    </div>
                    {error && (
                        <div className="alert error" role="alert">
                            {error}
                        </div>
                    )}
                    {children}
                </Dialog.Content>
            </Dialog.Portal>
        </Dialog.Root>
    );
}

function NumberField({
    label,
    value,
    onChange,
    min = 1,
    max = 65535,
}: {
    label: string;
    value: number;
    onChange: (n: number) => void;
    min?: number;
    max?: number;
}) {
    return (
        <label className="field">
            {label}
            <input
                type="number"
                min={min}
                max={max}
                required
                value={value}
                onChange={(e) => onChange(Number(e.target.value))}
            />
        </label>
    );
}

function TrafficChart({ samples }: { samples: HistorySample[] }) {
    if (!samples.length) return <div className="empty-chart">所选时段暂无历史样本</div>;
    const max = Math.max(1, ...samples.flatMap((s) => [s.bytesToTarget, s.bytesToCaller]));
    const line = (key: 'bytesToTarget' | 'bytesToCaller') =>
        samples
            .map(
                (s, i) => `${38 + (i * 680) / Math.max(1, samples.length - 1)},${210 - (s[key] / max) * 178}`,
            )
            .join(' ');
    return (
        <div className="chart-wrap">
            <svg viewBox="0 0 750 240" role="img" aria-label="目标和访问方历史累计流量折线图">
                <line x1="38" y1="210" x2="720" y2="210" className="axis" />
                <line x1="38" y1="32" x2="38" y2="210" className="axis" />
                <text x="3" y="38">
                    {fmt(max)}
                </text>
                <text x="7" y="215">
                    0
                </text>
                <polyline className="chart-target" points={line('bytesToTarget')} />
                <polyline className="chart-caller" points={line('bytesToCaller')} />
            </svg>
            <div className="chart-labels">
                <span>{time(samples[0].timestampUtc)}</span>
                <span>{time(samples.at(-1)?.timestampUtc)}</span>
            </div>
        </div>
    );
}

export function App() {
    const [session, setSession] = useState<Session>({ authenticated: false });
    const [overview, setOverview] = useState<Overview | null>(null);
    const [clients, setClients] = useState<ViewClient[]>([]);
    const [query, setQuery] = useState('');
    const [status, setStatus] = useState('all');
    const [busy, setBusy] = useState(true);
    const [error, setError] = useState('');
    const [notice, setNotice] = useState('');
    const [loginOpen, setLoginOpen] = useState(false);
    const [loginName, setLoginName] = useState('');
    const [loginPassword, setLoginPassword] = useState('');
    const [clientDialog, setClientDialog] = useState<{ original?: Client; form: ClientForm } | null>(null);
    const [channelDialog, setChannelDialog] = useState<{
        clientId: string;
        original?: Channel;
        form: ChannelForm;
    } | null>(null);
    const [mappingDialog, setMappingDialog] = useState<{
        clientId: string;
        original?: Mapping;
        form: MappingForm;
    } | null>(null);
    const [confirmDeleteMapping, setConfirmDeleteMapping] = useState<{
        clientId: string;
        mapping: Mapping;
    } | null>(null);
    const [historyDialog, setHistoryDialog] = useState<{
        client: ViewClient;
        channel: Channel;
        hours: number;
        samples: HistorySample[];
    } | null>(null);
    const [confirmDelete, setConfirmDelete] = useState<{ clientId: string; channel: Channel } | null>(null);
    const [saving, setSaving] = useState(false);
    const [updatedAt, setUpdatedAt] = useState(0);
    const [clock, setClock] = useState(Date.now());
    const editing = Boolean(
        loginOpen ||
        clientDialog ||
        channelDialog ||
        mappingDialog ||
        historyDialog ||
        confirmDelete ||
        confirmDeleteMapping,
    );
    const editingRef = useRef(editing);
    editingRef.current = editing;

    const refresh = useCallback(async () => {
        try {
            const [nextSession, nextOverview, firstPage] = await Promise.all([
                api<Session>('/api/v1/admin/session'),
                api<Overview>('/api/v1/overview'),
                api<{ clients: Client[]; total: number }>('/api/v1/clients?page=1&pageSize=100'),
            ]);
            const pages = await Promise.all(
                Array.from({ length: Math.ceil(firstPage.total / 100) - 1 }, (_, i) =>
                    api<{ clients: Client[] }>(`/api/v1/clients?page=${i + 2}&pageSize=100`),
                ),
            );
            const allClients = [...firstPage.clients, ...pages.flatMap((page) => page.clients)];
            const detailed = await Promise.all(
                allClients.map(async (client) => {
                    const [channels, mappings] = await Promise.all([
                        api<{ channels: Channel[] }>(`/api/v1/clients/${enc(client.clientId)}/channels`),
                        nextSession.authenticated
                            ? api<{ mappings: Mapping[] }>(
                                  `/api/v1/admin/clients/${enc(client.clientId)}/mappings`,
                              )
                            : Promise.resolve({ mappings: [] }),
                    ]);
                    return { ...client, channels: channels.channels, mappings: mappings.mappings };
                }),
            );
            setSession(nextSession);
            setOverview(nextOverview);
            setClients(detailed);
            setUpdatedAt(Date.now());
            setError('');
        } catch (e) {
            setError((e as Error).message);
        } finally {
            setBusy(false);
        }
    }, []);

    useEffect(() => {
        void refresh();
        const timer = window.setInterval(() => {
            setClock(Date.now());
            if (!editingRef.current) void refresh();
        }, 5000);
        return () => window.clearInterval(timer);
    }, [refresh]);
    const write = async <T,>(path: string, method: string, data?: unknown) =>
        api<T>(path, {
            method,
            headers: { 'Content-Type': 'application/json', 'X-RelayLink-CSRF': session.csrfToken || '' },
            body: data === undefined ? undefined : JSON.stringify(data),
        });

    async function login(e: FormEvent) {
        e.preventDefault();
        setSaving(true);
        setError('');
        try {
            const next = await api<Session>('/api/v1/admin/session', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ username: loginName, password: loginPassword }),
            });
            setSession(next);
            setLoginPassword('');
            setLoginOpen(false);
            setNotice('已登录，可以管理客户端与通道。');
            await refresh();
        } catch (e) {
            setError((e as Error).message);
        } finally {
            setSaving(false);
        }
    }
    async function logout() {
        try {
            await api('/api/v1/admin/session', { method: 'DELETE' });
            setSession({ authenticated: false });
            setNotice('已退出登录。');
        } catch (e) {
            setError((e as Error).message);
        }
    }
    async function saveClient(e: FormEvent) {
        e.preventDefault();
        if (!clientDialog) return;
        setSaving(true);
        setError('');
        try {
            const { original, form } = clientDialog;
            await write(
                `/api/v1/admin/clients${original ? `/${enc(original.clientId)}` : ''}`,
                original ? 'PUT' : 'POST',
                original
                    ? {
                          displayName: form.displayName,
                          enabled: form.enabled,
                          maxConnections: form.maxConnections,
                          maxPendingConnections: form.maxPendingConnections,
                      }
                    : form,
            );
            setClientDialog(null);
            setNotice(original ? '客户端设置已保存。' : '客户端已创建，可下载 Agent 配置。');
            await refresh();
        } catch (e) {
            setError((e as Error).message);
        } finally {
            setSaving(false);
        }
    }
    async function saveChannel(e: FormEvent) {
        e.preventDefault();
        if (!channelDialog) return;
        setSaving(true);
        setError('');
        try {
            const { clientId, original, form } = channelDialog;
            const result = await write<{ message: string }>(
                `/api/v1/admin/clients/${enc(clientId)}/channels${original ? `/${enc(original.channelId)}` : ''}`,
                original ? 'PUT' : 'POST',
                original ? { ...form, channelId: undefined } : form,
            );
            setChannelDialog(null);
            setNotice(result.message);
            await refresh();
        } catch (e) {
            setError((e as Error).message);
        } finally {
            setSaving(false);
        }
    }
    async function deleteChannel() {
        if (!confirmDelete) return;
        setSaving(true);
        setError('');
        try {
            const result = await write<{ message: string }>(
                `/api/v1/admin/clients/${enc(confirmDelete.clientId)}/channels/${enc(confirmDelete.channel.channelId)}`,
                'DELETE',
            );
            setConfirmDelete(null);
            setNotice(result.message);
            await refresh();
        } catch (e) {
            setError((e as Error).message);
        } finally {
            setSaving(false);
        }
    }
    async function saveMapping(e: FormEvent) {
        e.preventDefault();
        if (!mappingDialog) return;
        setSaving(true);
        setError('');
        try {
            const { clientId, original, form } = mappingDialog;
            await write(
                `/api/v1/admin/clients/${enc(clientId)}/mappings${original ? `/${enc(original.mappingId)}` : ''}`,
                original ? 'PUT' : 'POST',
                original ? { ...form, mappingId: undefined } : form,
            );
            setMappingDialog(null);
            setNotice('本机互访映射已保存并下发。');
            await refresh();
        } catch (e) {
            setError((e as Error).message);
        } finally {
            setSaving(false);
        }
    }
    async function deleteMapping() {
        if (!confirmDeleteMapping) return;
        setSaving(true);
        setError('');
        try {
            await write(
                `/api/v1/admin/clients/${enc(confirmDeleteMapping.clientId)}/mappings/${enc(confirmDeleteMapping.mapping.mappingId)}`,
                'DELETE',
            );
            setConfirmDeleteMapping(null);
            setNotice('互访映射已删除并下发。');
            await refresh();
        } catch (e) {
            setError((e as Error).message);
        } finally {
            setSaving(false);
        }
    }
    async function loadIdentityFingerprint(clientId: string) {
        try {
            const result = await api<{ e2eCertificateSha256?: string }>(
                `/api/v1/admin/clients/${enc(clientId)}/identity`,
            );
            if (!result.e2eCertificateSha256) throw new Error('客户端尚未在线登记端到端证书指纹。');
            setChannelDialog((current) =>
                current?.clientId === clientId
                    ? {
                          ...current,
                          form: { ...current.form, e2eCertificateSha256: result.e2eCertificateSha256! },
                      }
                    : current,
            );
            setNotice('已读取在线 Agent 指纹；启用前请与 Agent 本机指纹核对。');
        } catch (e) {
            setError((e as Error).message);
        }
    }
    async function showHistory(client: ViewClient, channel: Channel, hours = 24) {
        setHistoryDialog({ client, channel, hours, samples: [] });
        try {
            const result = await api<{ samples: HistorySample[] }>(
                `/api/v1/history?clientId=${enc(client.clientId)}&channelId=${enc(channel.channelId)}&hours=${hours}`,
            );
            setHistoryDialog((current) =>
                current &&
                current.client.clientId === client.clientId &&
                current.channel.channelId === channel.channelId
                    ? { ...current, hours, samples: result.samples }
                    : current,
            );
        } catch (e) {
            setError((e as Error).message);
        }
    }
    const visible = clients.filter(
        (c) =>
            (status === 'all' ||
                (status === 'online' && c.online && c.enabled) ||
                (status === 'offline' && !c.online && c.enabled) ||
                (status === 'disabled' && !c.enabled)) &&
            `${c.clientId} ${c.displayName} ${c.channels.map((ch) => `${ch.channelId} ${ch.displayName}`).join(' ')}`
                .toLowerCase()
                .includes(query.toLowerCase()),
    );
    const stale = Boolean(error || (updatedAt && clock - updatedAt > 10000));

    return (
        <div className="shell">
            <header className="topbar">
                <div className="brand">
                    <span className="brand-mark">
                        <ArrowLeftRight size={19} strokeWidth={2.5} />
                    </span>
                    <span>
                        RelayLink <small>CONTROL CENTER</small>
                    </span>
                </div>
                <div className="top-actions">
                    <span className="live-label">
                        <span className={`live-dot${stale ? ' stale' : ''}`} />{' '}
                        {stale ? '数据已过期' : '实时监控'}
                    </span>
                    {session.authenticated ? (
                        <>
                            <span className="auth-status">
                                <ShieldCheck size={15} /> 管理员
                            </span>
                            <button className="ghost" onClick={logout}>
                                <LogOut size={16} /> 退出
                            </button>
                        </>
                    ) : (
                        <button className="login-button" onClick={() => setLoginOpen(true)}>
                            <LockKeyhole size={15} /> 未登录 · 登录
                        </button>
                    )}
                </div>
            </header>
            <main className="main">
                <section className="page-intro">
                    <div>
                        <div className="eyebrow">OVERVIEW / 总览</div>
                        <h1>连接概览</h1>
                        <p>
                            查看节点状态、通道流量与历史趋势。{!session.authenticated && '登录后可编辑配置。'}
                        </p>
                    </div>
                    <button className="ghost refresh" onClick={() => void refresh()}>
                        <RefreshCw size={16} /> 刷新{' '}
                        <span>{overview ? time(overview.snapshotTimeUtc) : '—'}</span>
                    </button>
                </section>
                {error && (
                    <div className="alert error" role="alert">
                        <CircleHelp size={16} /> {error}
                        <button onClick={() => setError('')} aria-label="关闭错误">
                            <X size={15} />
                        </button>
                    </div>
                )}
                {notice && (
                    <div className="alert success" role="status">
                        <Check size={16} /> {notice}
                        <button onClick={() => setNotice('')} aria-label="关闭通知">
                            <X size={15} />
                        </button>
                    </div>
                )}
                <section className="metrics" aria-label="服务概况">
                    <div className="metric">
                        <div className="metric-icon blue">
                            <Cloud size={20} />
                        </div>
                        <div>
                            <span>在线客户端</span>
                            <strong>
                                {overview ? `${overview.clientsOnline} / ${overview.clientsTotal}` : '—'}
                            </strong>
                            <small>已连接 / 总数</small>
                        </div>
                    </div>
                    <div className="metric">
                        <div className="metric-icon violet">
                            <ArrowLeftRight size={20} />
                        </div>
                        <div>
                            <span>可用通道</span>
                            <strong>
                                {overview ? `${overview.channelsAvailable} / ${overview.channelsTotal}` : '—'}
                            </strong>
                            <small>可接入 / 总数</small>
                        </div>
                    </div>
                    <div className="metric">
                        <div className="metric-icon green">
                            <Activity size={20} />
                        </div>
                        <div>
                            <span>转发中连接</span>
                            <strong>{overview?.activeConnections ?? '—'}</strong>
                            <small>当前活动连接</small>
                        </div>
                    </div>
                    <div className="metric wide">
                        <div className="metric-icon orange">
                            <ArrowDownToLine size={20} />
                        </div>
                        <div>
                            <span>本次运行累计流量</span>
                            <strong>
                                {overview
                                    ? `${fmt(overview.bytesToTarget)} / ${fmt(overview.bytesToCaller)}`
                                    : '—'}
                            </strong>
                            <small>
                                → 目标 / → 访问方 · 自 {overview ? time(overview.statsSinceUtc) : '—'}
                            </small>
                            <small title="互访通道采用端到端 TLS，服务端仅能统计转发的密文字节">
                                互访密文：
                                {overview
                                    ? `${fmt(overview.peerCiphertextToTarget)} / ${fmt(overview.peerCiphertextToCaller)}`
                                    : '—'}
                            </small>
                        </div>
                    </div>
                </section>
                <section className="section-head">
                    <div>
                        <div className="eyebrow">NODES & CHANNELS / 节点与通道</div>
                        <h2>
                            客户端列表 <span>{clients.length}</span>
                        </h2>
                    </div>
                    {session.authenticated && (
                        <button
                            className="primary"
                            onClick={() => setClientDialog({ form: { ...emptyClient } })}
                        >
                            <Plus size={17} /> 添加客户端
                        </button>
                    )}
                </section>
                <div className="filters">
                    <label className="search">
                        <Search size={17} />
                        <input
                            placeholder="搜索客户端或通道"
                            value={query}
                            onChange={(e) => setQuery(e.target.value)}
                        />
                    </label>
                    <label className="status-filter">
                        <span>状态</span>
                        <select value={status} onChange={(e) => setStatus(e.target.value)}>
                            <option value="all">全部状态</option>
                            <option value="online">在线</option>
                            <option value="offline">离线</option>
                            <option value="disabled">已禁用</option>
                        </select>
                        <ChevronDown size={14} />
                    </label>
                    <span className="result-count">显示 {visible.length} 个客户端</span>
                </div>
                <div className="clients">
                    {busy && !overview ? (
                        <div className="empty">正在加载数据…</div>
                    ) : visible.length === 0 ? (
                        <div className="empty">没有匹配的客户端</div>
                    ) : (
                        visible.map((client) => (
                            <article className="client-card" key={client.clientId}>
                                <div className="client-heading">
                                    <div className="client-title">
                                        <span className="client-avatar">
                                            {client.displayName.slice(0, 1).toUpperCase()}
                                        </span>
                                        <div>
                                            <div className="client-name">
                                                {client.displayName}{' '}
                                                <span
                                                    className={`badge ${!client.enabled ? 'disabled' : client.online ? 'online' : 'offline'}`}
                                                >
                                                    <span />
                                                    {!client.enabled
                                                        ? '已禁用'
                                                        : client.online
                                                          ? '在线'
                                                          : '离线'}
                                                </span>
                                            </div>
                                            <small>
                                                {client.clientId} · 最近心跳 {time(client.lastHeartbeatUtc)}
                                            </small>
                                        </div>
                                    </div>
                                    {session.authenticated && (
                                        <div className="client-actions">
                                            <button
                                                className="ghost"
                                                onClick={() => {
                                                    window.location.href = `/api/v1/admin/clients/${enc(client.clientId)}/agent-config`;
                                                }}
                                            >
                                                <ArrowDownToLine size={15} /> 下载配置
                                            </button>
                                            <button
                                                className="ghost"
                                                onClick={() =>
                                                    setClientDialog({
                                                        original: client,
                                                        form: {
                                                            clientId: client.clientId,
                                                            displayName: client.displayName,
                                                            enabled: client.enabled,
                                                            maxConnections: client.maxConnections,
                                                            maxPendingConnections:
                                                                client.maxPendingConnections,
                                                            agentServerHost: '',
                                                            trustedCaPemPath: '',
                                                        },
                                                    })
                                                }
                                            >
                                                <Settings2 size={15} /> 编辑客户端
                                            </button>
                                            <button
                                                className="primary subtle"
                                                onClick={() =>
                                                    setChannelDialog({
                                                        clientId: client.clientId,
                                                        form: { ...emptyChannel },
                                                    })
                                                }
                                            >
                                                <Plus size={15} /> 添加通道
                                            </button>
                                            <button
                                                className="ghost"
                                                onClick={() =>
                                                    setMappingDialog({
                                                        clientId: client.clientId,
                                                        form: { ...emptyMapping },
                                                    })
                                                }
                                            >
                                                <Plus size={15} /> 添加互访入口
                                            </button>
                                        </div>
                                    )}
                                </div>
                                <div className="table-scroll">
                                    <table>
                                        <thead>
                                            <tr>
                                                <th className="channel-col">通道</th>
                                                <th>云端监听</th>
                                                <th>目标地址</th>
                                                <th>状态</th>
                                                <th title="建连中：正在连接目标；转发中：正在传输">
                                                    建连中 / 转发中
                                                </th>
                                                <th>本次运行累计流量</th>
                                                {session.authenticated && (
                                                    <th className="action-col">操作</th>
                                                )}
                                            </tr>
                                        </thead>
                                        <tbody>
                                            {client.channels.length ? (
                                                client.channels.map((ch) => (
                                                    <tr key={ch.channelId}>
                                                        <td className="channel-col">
                                                            <strong title={ch.displayName}>
                                                                {ch.displayName}
                                                            </strong>
                                                            <small>{ch.channelId}</small>
                                                        </td>
                                                        <td className="mono">
                                                            {ch.authorizedClientsOnly
                                                                ? '仅客户端互访'
                                                                : `${ch.listenAddress}:${ch.listenPort}`}
                                                        </td>
                                                        <td className="mono">
                                                            {ch.targetHost}:{ch.targetPort}
                                                        </td>
                                                        <td>
                                                            <span
                                                                className={`state ${!ch.enabled ? 'muted' : ch.available ? 'good' : 'warn'}`}
                                                            >
                                                                <span />
                                                                {!ch.enabled
                                                                    ? '已禁用'
                                                                    : ch.authorizedClientsOnly
                                                                      ? '互访专用'
                                                                      : ch.available
                                                                        ? '可接入'
                                                                        : '不可接入'}
                                                            </span>
                                                        </td>
                                                        <td className="mono">
                                                            {ch.pendingConnections} / {ch.activeConnections}
                                                        </td>
                                                        <td>
                                                            {ch.authorizedClientsOnly ? (
                                                                <span title="端到端 TLS 密文字节；服务端无法统计业务有效载荷">
                                                                    {fmt(ch.peerCiphertextToTarget)} /{' '}
                                                                    {fmt(ch.peerCiphertextToCaller)}
                                                                </span>
                                                            ) : (
                                                                <button
                                                                    className="flow"
                                                                    title="查看此通道历史流量"
                                                                    onClick={() =>
                                                                        void showHistory(client, ch)
                                                                    }
                                                                >
                                                                    {fmt(ch.bytesToTarget)} /{' '}
                                                                    {fmt(ch.bytesToCaller)}
                                                                </button>
                                                            )}
                                                        </td>
                                                        {session.authenticated && (
                                                            <td>
                                                                <div className="row-actions">
                                                                    <button
                                                                        className="text-button"
                                                                        onClick={() =>
                                                                            setChannelDialog({
                                                                                clientId: client.clientId,
                                                                                original: ch,
                                                                                form: {
                                                                                    channelId: ch.channelId,
                                                                                    displayName:
                                                                                        ch.displayName,
                                                                                    enabled: ch.enabled,
                                                                                    listenAddress:
                                                                                        ch.listenAddress,
                                                                                    listenPort: ch.listenPort,
                                                                                    targetHost: ch.targetHost,
                                                                                    targetPort: ch.targetPort,
                                                                                    maxConnections:
                                                                                        ch.maxConnections,
                                                                                    targetConnectTimeoutSeconds:
                                                                                        ch.targetConnectTimeoutSeconds,
                                                                                    authorizedClientsOnly:
                                                                                        Boolean(
                                                                                            ch.authorizedClientsOnly,
                                                                                        ),
                                                                                    e2eCertificateSha256:
                                                                                        ch.e2eCertificateSha256 ||
                                                                                        '',
                                                                                },
                                                                            })
                                                                        }
                                                                    >
                                                                        编辑
                                                                    </button>
                                                                    <button
                                                                        className="text-button danger"
                                                                        onClick={() =>
                                                                            setConfirmDelete({
                                                                                clientId: client.clientId,
                                                                                channel: ch,
                                                                            })
                                                                        }
                                                                    >
                                                                        删除
                                                                    </button>
                                                                </div>
                                                            </td>
                                                        )}
                                                    </tr>
                                                ))
                                            ) : (
                                                <tr>
                                                    <td
                                                        colSpan={session.authenticated ? 7 : 6}
                                                        className="empty-row"
                                                    >
                                                        暂无通道
                                                    </td>
                                                </tr>
                                            )}
                                        </tbody>
                                    </table>
                                </div>
                                {session.authenticated && client.mappings.length > 0 && (
                                    <div className="mapping-list">
                                        <strong>本机互访入口</strong>
                                        {client.mappings.map((mapping) => (
                                            <div className="mapping-row" key={mapping.mappingId}>
                                                <span>
                                                    {mapping.mappingId} ·{' '}
                                                    {mapping.available && mapping.localPort
                                                        ? `127.0.0.1:${mapping.localPort}`
                                                        : mapping.enabled
                                                          ? '等待 Agent 上报地址'
                                                          : '已禁用'}{' '}
                                                    → {mapping.targetClientId}/{mapping.targetChannelId} ·{' '}
                                                    {mapping.enabled ? '启用' : '禁用'}
                                                </span>
                                                <button
                                                    className="text-button"
                                                    onClick={() =>
                                                        setMappingDialog({
                                                            clientId: client.clientId,
                                                            original: mapping,
                                                            form: {
                                                                mappingId: mapping.mappingId,
                                                                enabled: mapping.enabled,
                                                                targetClientId: mapping.targetClientId,
                                                                targetChannelId: mapping.targetChannelId,
                                                            },
                                                        })
                                                    }
                                                >
                                                    编辑
                                                </button>
                                                <button
                                                    className="text-button danger"
                                                    onClick={() =>
                                                        setConfirmDeleteMapping({
                                                            clientId: client.clientId,
                                                            mapping,
                                                        })
                                                    }
                                                >
                                                    删除
                                                </button>
                                            </div>
                                        ))}
                                    </div>
                                )}
                            </article>
                        ))
                    )}
                </div>
                <footer>
                    RelayLink · 安全的内网 TCP 连接管理 <span>数据每 5 秒更新，编辑时暂停自动刷新</span>
                </footer>
            </main>

            <Modal
                title="管理员登录"
                description="登录后可修改客户端与通道配置。"
                open={loginOpen}
                onClose={() => setLoginOpen(false)}
                error={error}
            >
                <form onSubmit={login} className="form">
                    <label className="field">
                        用户名
                        <input
                            autoFocus
                            autoComplete="username"
                            required
                            value={loginName}
                            onChange={(e) => setLoginName(e.target.value)}
                        />
                    </label>
                    <label className="field">
                        密码
                        <input
                            type="password"
                            autoComplete="current-password"
                            required
                            value={loginPassword}
                            onChange={(e) => setLoginPassword(e.target.value)}
                        />
                    </label>
                    <div className="form-actions">
                        <button type="button" className="ghost" onClick={() => setLoginOpen(false)}>
                            取消
                        </button>
                        <button className="primary" disabled={saving}>
                            登录
                        </button>
                    </div>
                </form>
            </Modal>

            <Modal
                title={clientDialog?.original ? '编辑客户端' : '添加客户端'}
                description={
                    clientDialog?.original
                        ? '修改后立即保存到服务端配置。'
                        : '创建后可下载包含独立密钥的 Agent 配置。'
                }
                open={!!clientDialog}
                onClose={() => setClientDialog(null)}
                error={error}
            >
                {clientDialog && (
                    <form className="form" onSubmit={saveClient}>
                        <div className="form-grid">
                            <label className="field">
                                客户端 ID
                                <input
                                    required
                                    disabled={!!clientDialog.original}
                                    value={clientDialog.form.clientId}
                                    onChange={(e) =>
                                        setClientDialog({
                                            ...clientDialog,
                                            form: { ...clientDialog.form, clientId: e.target.value },
                                        })
                                    }
                                />
                            </label>
                            <label className="field">
                                显示名称
                                <input
                                    required
                                    value={clientDialog.form.displayName}
                                    onChange={(e) =>
                                        setClientDialog({
                                            ...clientDialog,
                                            form: { ...clientDialog.form, displayName: e.target.value },
                                        })
                                    }
                                />
                            </label>
                            <NumberField
                                label="最大连接数"
                                value={clientDialog.form.maxConnections}
                                onChange={(n) =>
                                    setClientDialog({
                                        ...clientDialog,
                                        form: { ...clientDialog.form, maxConnections: n },
                                    })
                                }
                                max={1000000}
                            />
                            <NumberField
                                label="最大等待连接数"
                                value={clientDialog.form.maxPendingConnections}
                                onChange={(n) =>
                                    setClientDialog({
                                        ...clientDialog,
                                        form: { ...clientDialog.form, maxPendingConnections: n },
                                    })
                                }
                                max={1000000}
                            />
                            {!clientDialog.original && (
                                <>
                                    <label className="field">
                                        Agent 连接的服务端主机
                                        <input
                                            required
                                            value={clientDialog.form.agentServerHost}
                                            onChange={(e) =>
                                                setClientDialog({
                                                    ...clientDialog,
                                                    form: {
                                                        ...clientDialog.form,
                                                        agentServerHost: e.target.value,
                                                    },
                                                })
                                            }
                                        />
                                    </label>
                                    <label className="field">
                                        受信 CA 路径（启用 TLS 时必填）
                                        <input
                                            value={clientDialog.form.trustedCaPemPath}
                                            onChange={(e) =>
                                                setClientDialog({
                                                    ...clientDialog,
                                                    form: {
                                                        ...clientDialog.form,
                                                        trustedCaPemPath: e.target.value,
                                                    },
                                                })
                                            }
                                        />
                                    </label>
                                </>
                            )}
                        </div>
                        <label className="check">
                            <input
                                type="checkbox"
                                checked={clientDialog.form.enabled}
                                onChange={(e) =>
                                    setClientDialog({
                                        ...clientDialog,
                                        form: { ...clientDialog.form, enabled: e.target.checked },
                                    })
                                }
                            />{' '}
                            启用客户端
                        </label>
                        <div className="form-actions">
                            <button type="button" className="ghost" onClick={() => setClientDialog(null)}>
                                取消
                            </button>
                            <button className="primary" disabled={saving}>
                                保存客户端
                            </button>
                        </div>
                    </form>
                )}
            </Modal>

            <Modal
                title={channelDialog?.original ? '编辑通道' : '添加通道'}
                description="保存后立即更新服务端监听，并向在线客户端下发配置。"
                open={!!channelDialog}
                onClose={() => setChannelDialog(null)}
                error={error}
            >
                {channelDialog && (
                    <form className="form" onSubmit={saveChannel}>
                        <div className="form-grid">
                            <label className="field">
                                通道 ID
                                <input
                                    required
                                    disabled={!!channelDialog.original}
                                    value={channelDialog.form.channelId}
                                    onChange={(e) =>
                                        setChannelDialog({
                                            ...channelDialog,
                                            form: { ...channelDialog.form, channelId: e.target.value },
                                        })
                                    }
                                />
                            </label>
                            <label className="field">
                                显示名称
                                <input
                                    required
                                    value={channelDialog.form.displayName}
                                    onChange={(e) =>
                                        setChannelDialog({
                                            ...channelDialog,
                                            form: { ...channelDialog.form, displayName: e.target.value },
                                        })
                                    }
                                />
                            </label>
                            <label className="field">
                                云端监听地址
                                <input
                                    required
                                    value={channelDialog.form.listenAddress}
                                    onChange={(e) =>
                                        setChannelDialog({
                                            ...channelDialog,
                                            form: { ...channelDialog.form, listenAddress: e.target.value },
                                        })
                                    }
                                />
                            </label>
                            <NumberField
                                label="云端监听端口"
                                value={channelDialog.form.listenPort}
                                onChange={(n) =>
                                    setChannelDialog({
                                        ...channelDialog,
                                        form: { ...channelDialog.form, listenPort: n },
                                    })
                                }
                            />
                            <label className="field">
                                目标主机
                                <input
                                    required
                                    value={channelDialog.form.targetHost}
                                    onChange={(e) =>
                                        setChannelDialog({
                                            ...channelDialog,
                                            form: { ...channelDialog.form, targetHost: e.target.value },
                                        })
                                    }
                                />
                            </label>
                            <NumberField
                                label="目标端口"
                                value={channelDialog.form.targetPort}
                                onChange={(n) =>
                                    setChannelDialog({
                                        ...channelDialog,
                                        form: { ...channelDialog.form, targetPort: n },
                                    })
                                }
                            />
                            <NumberField
                                label="最大连接数"
                                value={channelDialog.form.maxConnections}
                                onChange={(n) =>
                                    setChannelDialog({
                                        ...channelDialog,
                                        form: { ...channelDialog.form, maxConnections: n },
                                    })
                                }
                                max={1000000}
                            />
                            <NumberField
                                label="目标连接超时（秒）"
                                value={channelDialog.form.targetConnectTimeoutSeconds}
                                onChange={(n) =>
                                    setChannelDialog({
                                        ...channelDialog,
                                        form: { ...channelDialog.form, targetConnectTimeoutSeconds: n },
                                    })
                                }
                                max={3600}
                            />
                            {channelDialog.form.authorizedClientsOnly && (
                                <label className="field">
                                    被访问 Agent 端到端证书 SHA-256 指纹
                                    <input
                                        required
                                        pattern="[A-Fa-f0-9]{64}"
                                        value={channelDialog.form.e2eCertificateSha256}
                                        onChange={(e) =>
                                            setChannelDialog({
                                                ...channelDialog,
                                                form: {
                                                    ...channelDialog.form,
                                                    e2eCertificateSha256: e.target.value,
                                                },
                                            })
                                        }
                                    />
                                    <button
                                        type="button"
                                        className="ghost"
                                        onClick={() => void loadIdentityFingerprint(channelDialog.clientId)}
                                    >
                                        读取当前在线 Agent 指纹
                                    </button>
                                </label>
                            )}
                        </div>
                        <label className="check">
                            <input
                                type="checkbox"
                                checked={channelDialog.form.authorizedClientsOnly}
                                onChange={(e) =>
                                    setChannelDialog({
                                        ...channelDialog,
                                        form: {
                                            ...channelDialog.form,
                                            authorizedClientsOnly: e.target.checked,
                                        },
                                    })
                                }
                            />{' '}
                            仅允许授权客户端互访（停止云端代理监听；服务端自动生成 32 字节访问密钥）
                        </label>
                        <label className="check">
                            <input
                                type="checkbox"
                                checked={channelDialog.form.enabled}
                                onChange={(e) =>
                                    setChannelDialog({
                                        ...channelDialog,
                                        form: { ...channelDialog.form, enabled: e.target.checked },
                                    })
                                }
                            />{' '}
                            启用通道
                        </label>
                        <div className="form-actions">
                            <button type="button" className="ghost" onClick={() => setChannelDialog(null)}>
                                取消
                            </button>
                            <button className="primary" disabled={saving}>
                                保存并下发
                            </button>
                        </div>
                    </form>
                )}
            </Modal>

            <Modal
                title={mappingDialog?.original ? '编辑互访入口' : '添加互访入口'}
                description="映射和访问密钥由服务端管理并下发；仅监听本机 127.0.0.1。"
                open={!!mappingDialog}
                onClose={() => setMappingDialog(null)}
                error={error}
            >
                {mappingDialog && (
                    <form className="form" onSubmit={saveMapping}>
                        <div className="form-grid">
                            <label className="field">
                                映射 ID
                                <input
                                    required
                                    disabled={!!mappingDialog.original}
                                    value={mappingDialog.form.mappingId}
                                    onChange={(e) =>
                                        setMappingDialog({
                                            ...mappingDialog,
                                            form: { ...mappingDialog.form, mappingId: e.target.value },
                                        })
                                    }
                                />
                            </label>
                            <p className="field-hint">
                                本机端口由 Agent 自动选择并保存，冲突时自动轮换；上线后显示实际地址。
                            </p>
                            <label className="field">
                                目标客户端
                                <select
                                    required
                                    value={mappingDialog.form.targetClientId}
                                    onChange={(e) =>
                                        setMappingDialog({
                                            ...mappingDialog,
                                            form: {
                                                ...mappingDialog.form,
                                                targetClientId: e.target.value,
                                                targetChannelId: '',
                                            },
                                        })
                                    }
                                >
                                    <option value="">请选择</option>
                                    {clients
                                        .filter((candidate) => candidate.clientId !== mappingDialog.clientId)
                                        .map((candidate) => (
                                            <option value={candidate.clientId} key={candidate.clientId}>
                                                {candidate.displayName} ({candidate.clientId})
                                            </option>
                                        ))}
                                </select>
                            </label>
                            <label className="field">
                                目标授权通道
                                <select
                                    required
                                    value={mappingDialog.form.targetChannelId}
                                    onChange={(e) =>
                                        setMappingDialog({
                                            ...mappingDialog,
                                            form: { ...mappingDialog.form, targetChannelId: e.target.value },
                                        })
                                    }
                                >
                                    <option value="">请选择</option>
                                    {clients
                                        .find(
                                            (candidate) =>
                                                candidate.clientId === mappingDialog.form.targetClientId,
                                        )
                                        ?.channels.filter(
                                            (channel) => channel.enabled && channel.authorizedClientsOnly,
                                        )
                                        .map((channel) => (
                                            <option value={channel.channelId} key={channel.channelId}>
                                                {channel.displayName} ({channel.channelId})
                                            </option>
                                        ))}
                                </select>
                            </label>
                        </div>
                        <label className="check">
                            <input
                                type="checkbox"
                                checked={mappingDialog.form.enabled}
                                onChange={(e) =>
                                    setMappingDialog({
                                        ...mappingDialog,
                                        form: { ...mappingDialog.form, enabled: e.target.checked },
                                    })
                                }
                            />{' '}
                            启用互访入口
                        </label>
                        <div className="form-actions">
                            <button type="button" className="ghost" onClick={() => setMappingDialog(null)}>
                                取消
                            </button>
                            <button className="primary" disabled={saving}>
                                保存并下发
                            </button>
                        </div>
                    </form>
                )}
            </Modal>

            <Modal
                title="删除互访入口？"
                description="删除后访问方 Agent 将停止对应本机监听。"
                open={!!confirmDeleteMapping}
                onClose={() => setConfirmDeleteMapping(null)}
                error={error}
            >
                <p>确定删除「{confirmDeleteMapping?.mapping.mappingId}」？</p>
                <div className="form-actions">
                    <button className="ghost" onClick={() => setConfirmDeleteMapping(null)}>
                        取消
                    </button>
                    <button
                        className="primary danger-fill"
                        disabled={saving}
                        onClick={() => void deleteMapping()}
                    >
                        删除入口
                    </button>
                </div>
            </Modal>

            <Modal
                title={`${historyDialog?.client.displayName ?? ''} / ${historyDialog?.channel.displayName ?? ''}`}
                description="按客户端与通道聚合的历史流量。曲线为服务每次运行内的累计值，重启后归零。"
                open={!!historyDialog}
                onClose={() => setHistoryDialog(null)}
                error={error}
            >
                {historyDialog && (
                    <div>
                        <div className="history-toolbar">
                            <div>
                                <span className="legend target" /> → 目标　
                                <span className="legend caller" /> → 访问方
                            </div>
                            <select
                                value={historyDialog.hours}
                                onChange={(e) =>
                                    void showHistory(
                                        historyDialog.client,
                                        historyDialog.channel,
                                        Number(e.target.value),
                                    )
                                }
                            >
                                <option value="24">最近 24 小时</option>
                                <option value="168">最近 7 天</option>
                                <option value="720">最近 30 天</option>
                            </select>
                        </div>
                        <TrafficChart samples={historyDialog.samples} />
                        <div className="history-total">
                            最新累计　→ 目标 <b>{fmt(historyDialog.samples.at(-1)?.bytesToTarget || 0)}</b>　→
                            访问方 <b>{fmt(historyDialog.samples.at(-1)?.bytesToCaller || 0)}</b>
                        </div>
                    </div>
                )}
            </Modal>

            <Modal
                title="删除通道？"
                description="删除后服务端监听立即停止，并向在线客户端下发更新。"
                open={!!confirmDelete}
                onClose={() => setConfirmDelete(null)}
                error={error}
            >
                <p>确定删除通道「{confirmDelete?.channel.displayName}」？此操作不可撤销。</p>
                <div className="form-actions">
                    <button className="ghost" onClick={() => setConfirmDelete(null)}>
                        取消
                    </button>
                    <button
                        className="primary danger-fill"
                        disabled={saving}
                        onClick={() => void deleteChannel()}
                    >
                        删除通道
                    </button>
                </div>
            </Modal>
        </div>
    );
}

const root = document.getElementById('root');
if (root) createRoot(root).render(<App />);
