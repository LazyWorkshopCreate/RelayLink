import { useCallback, useEffect, useRef, useState, type FormEvent, type ReactNode } from 'react';
import { createRoot } from 'react-dom/client';
import * as Dialog from '@radix-ui/react-dialog';
import {
    Activity,
    ClipboardList,
    ArrowDownToLine,
    ArrowLeftRight,
    Check,
    ChevronDown,
    CircleAlert,
    CircleHelp,
    Cloud,
    LockKeyhole,
    LogOut,
    Plus,
    RefreshCw,
    Search,
    Settings2,
    ShieldCheck,
    Shield,
    Trash2,
    X,
} from 'lucide-react';
import './styles.css';

type Session = { authenticated: boolean; csrfToken?: string };
type AgentDefaults = { serverHost: string | null; serverPort: number; useTls: boolean };
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
    tags: string[];
    enabled: boolean;
    online: boolean;
    e2eCertificateSha256?: string;
    lastHeartbeatUtc?: string;
    maxConnections: number;
    maxPendingConnections: number;
};
type Channel = {
    channelId: string;
    displayName: string;
    tags: string[];
    enabled: boolean;
    authorizedClientsOnly?: boolean;
    endToEndEncryptionEnabled?: boolean;
    securityGroupId?: string | null;
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
type SecurityGroup = { id: string; name: string; entries: string[] };
type SecurityGroupForm = { id: string; name: string; entries: string };
type HistorySample = { timestampUtc: string; bytesToTarget: number; bytesToCaller: number };
type LiveConnection = {
    connectionId: string;
    kind: string;
    state: string;
    source: string;
    startedAtUtc: string;
    bytesToTarget: number;
    bytesToCaller: number;
};
type AuditRow = {
    eventId: string;
    occurredAtUtc: string;
    eventType: string;
    outcome: string;
    reasonCode?: string;
    connectionId?: string;
    sessionId?: string;
    clientId?: string;
    channelId?: string;
    mappingId?: string;
    callerClientId?: string;
    targetClientId?: string;
    actor?: string;
    remoteIp?: string;
    durationMs?: number;
    bytesToTarget: number;
    bytesToCaller: number;
};
type AuditPage = { total: number; events: AuditRow[] };
const auditLabels: Record<string, string> = {
    admin_login_success: '管理员登录成功',
    admin_login_failure: '管理员登录失败',
    admin_logout: '管理员退出',
    agent_session_ready: 'Agent 上线',
    agent_session_closed: 'Agent 离线',
    agent_session_rejected: 'Agent 认证拒绝',
    proxy_connection_opened: '普通连接建立',
    proxy_connection_closed: '普通连接断开',
    proxy_connection_rejected: '普通连接拒绝',
    peer_connection_opened: '端到端连接建立',
    peer_connection_closed: '端到端连接断开',
    peer_connection_rejected: '端到端连接拒绝',
};
type ViewClient = Client & { channels: Channel[]; mappings: Mapping[] };
type DashboardSnapshot = {
    snapshotTimeUtc: string;
    page: number;
    pageSize: number;
    total: number;
    overview: Overview;
    clients: ViewClient[];
};
type ClientForm = {
    clientId: string;
    displayName: string;
    tags: string;
    enabled: boolean;
    maxConnections: number;
    maxPendingConnections: number;
    agentServerHost: string;
};
type ChannelForm = {
    channelId: string;
    displayName: string;
    tags: string;
    enabled: boolean;
    listenAddress: string;
    listenPort: number;
    targetHost: string;
    targetPort: number;
    maxConnections: number;
    targetConnectTimeoutSeconds: number;
    authorizedClientsOnly: boolean;
    endToEndEncryptionEnabled: boolean;
    securityGroupId: string;
};
type MappingForm = {
    targetClientId: string;
    targetChannelId: string;
};

const emptyClient: ClientForm = {
    clientId: '',
    displayName: '',
    tags: '',
    enabled: true,
    maxConnections: 100,
    maxPendingConnections: 100,
    agentServerHost: '',
};
const emptyChannel: ChannelForm = {
    channelId: '',
    displayName: '',
    tags: '',
    enabled: true,
    listenAddress: '0.0.0.0',
    listenPort: 19000,
    targetHost: '127.0.0.1',
    targetPort: 19001,
    maxConnections: 100,
    targetConnectTimeoutSeconds: 10,
    authorizedClientsOnly: false,
    endToEndEncryptionEnabled: true,
    securityGroupId: '',
};
const emptyMapping: MappingForm = {
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
const parseTags = (value: string) => [
    ...new Map(
        value
            .split(/[,，\n]+/)
            .map((tag) => tag.trim())
            .filter(Boolean)
            .map((tag) => [tag.toLowerCase(), tag]),
    ).values(),
];
const hasAllTags = (tags: string[] | undefined, required: string[]) => {
    const normalized = new Set((tags || []).map((tag) => tag.toLowerCase()));
    return required.every((tag) => normalized.has(tag.toLowerCase()));
};

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
    wide,
    children,
}: {
    title: string;
    description?: string;
    open: boolean;
    onClose: () => void;
    error?: string;
    wide?: boolean;
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
                <Dialog.Content className={wide ? 'modal audit-modal' : 'modal'}>
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
            <svg viewBox="0 0 750 240" role="img" aria-label="目标和访问方每分钟流量折线图">
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
    const [securityGroups, setSecurityGroups] = useState<SecurityGroup[]>([]);
    const [securityGroupsOpen, setSecurityGroupsOpen] = useState(false);
    const [auditOpen, setAuditOpen] = useState(false);
    const [auditPage, setAuditPage] = useState<AuditPage>({ total: 0, events: [] });
    const [auditPageNumber, setAuditPageNumber] = useState(1);
    const [auditHours, setAuditHours] = useState(24);
    const [auditType, setAuditType] = useState('');
    const [auditClient, setAuditClient] = useState('');
    const [auditBusy, setAuditBusy] = useState(false);
    const [securityGroupForm, setSecurityGroupForm] = useState<SecurityGroupForm | null>(null);
    const [securityGroupOriginalId, setSecurityGroupOriginalId] = useState<string | null>(null);
    const [securityGroupToDelete, setSecurityGroupToDelete] = useState<SecurityGroup | null>(null);
    const [query, setQuery] = useState('');
    const [clientTagQuery, setClientTagQuery] = useState('');
    const [channelTagQuery, setChannelTagQuery] = useState('');
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
    const [connectionDialog, setConnectionDialog] = useState<{
        client: ViewClient;
        channel: Channel;
        connections: LiveConnection[];
        loading: boolean;
    } | null>(null);
    const [confirmConnectionId, setConfirmConnectionId] = useState<string | null>(null);
    const [confirmDelete, setConfirmDelete] = useState<{ clientId: string; channel: Channel } | null>(null);
    const [confirmDeleteClient, setConfirmDeleteClient] = useState<ViewClient | null>(null);
    const [saving, setSaving] = useState(false);
    const [updatedAt, setUpdatedAt] = useState(0);
    const [clock, setClock] = useState(Date.now());
    const editing = Boolean(
        loginOpen ||
        clientDialog ||
        channelDialog ||
        mappingDialog ||
        historyDialog ||
        connectionDialog ||
        confirmDelete ||
        confirmDeleteClient ||
        confirmDeleteMapping ||
        securityGroupsOpen ||
        auditOpen ||
        securityGroupToDelete,
    );
    const editingRef = useRef(editing);
    const refreshInFlightRef = useRef<Promise<void> | null>(null);
    editingRef.current = editing;

    const refresh = useCallback(() => {
        if (refreshInFlightRef.current) return refreshInFlightRef.current;
        const pending = (async () => {
            try {
                const [nextSession, firstPage] = await Promise.all([
                    api<Session>('/api/v1/admin/session'),
                    api<DashboardSnapshot>('/api/v1/dashboard/snapshot?page=1&pageSize=100'),
                ]);
                const allClients = [...firstPage.clients];
                const pageCount = Math.ceil(firstPage.total / firstPage.pageSize);
                for (let page = 2; page <= pageCount; page++) {
                    const nextPage = await api<DashboardSnapshot>(
                        `/api/v1/dashboard/snapshot?page=${page}&pageSize=${firstPage.pageSize}`,
                    );
                    allClients.push(...nextPage.clients);
                }
                setSession(nextSession);
                setOverview(firstPage.overview);
                setClients(allClients);
                setUpdatedAt(Date.now());
                setError('');
            } catch (e) {
                setError((e as Error).message);
            } finally {
                setBusy(false);
            }
        })().finally(() => {
            refreshInFlightRef.current = null;
        });
        refreshInFlightRef.current = pending;
        return pending;
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

    async function loadSecurityGroups() {
        const response = await api<{ securityGroups: SecurityGroup[] }>('/api/v1/admin/security-groups');
        setSecurityGroups(response.securityGroups);
    }

    async function openSecurityGroups() {
        setError('');
        try {
            await loadSecurityGroups();
            setSecurityGroupsOpen(true);
        } catch (e) {
            setError((e as Error).message);
        }
    }
    async function loadAudit(page = 1, hours = auditHours, eventType = auditType, clientId = auditClient) {
        setAuditBusy(true);
        setError('');
        try {
            const result = await api<AuditPage>(
                `/api/v1/admin/audit?hours=${hours}&page=${page}&pageSize=50&eventType=${enc(eventType)}&clientId=${enc(clientId.trim())}`,
            );
            setAuditPage(result);
            setAuditPageNumber(page);
        } catch (e) {
            setAuditPage({ total: 0, events: [] });
            setError((e as Error).message);
        } finally {
            setAuditBusy(false);
        }
    }
    function openAudit() {
        setAuditPage({ total: 0, events: [] });
        setAuditOpen(true);
        void loadAudit();
    }

    async function saveSecurityGroup(e: FormEvent) {
        e.preventDefault();
        if (!securityGroupForm) return;
        setSaving(true);
        setError('');
        try {
            const entries = securityGroupForm.entries
                .split(/[\n,]+/)
                .map((entry) => entry.trim())
                .filter(Boolean);
            await write(
                `/api/v1/admin/security-groups${securityGroupOriginalId ? `/${enc(securityGroupOriginalId)}` : ''}`,
                securityGroupOriginalId ? 'PUT' : 'POST',
                securityGroupOriginalId
                    ? { name: securityGroupForm.name, entries }
                    : { id: securityGroupForm.id, name: securityGroupForm.name, entries },
            );
            setSecurityGroupForm(null);
            setSecurityGroupOriginalId(null);
            await loadSecurityGroups();
            setNotice('安全组已保存，访问规则立即生效。');
        } catch (e) {
            setError((e as Error).message);
        } finally {
            setSaving(false);
        }
    }

    async function deleteSecurityGroup() {
        if (!securityGroupToDelete) return;
        setSaving(true);
        setError('');
        try {
            await write(`/api/v1/admin/security-groups/${enc(securityGroupToDelete.id)}`, 'DELETE');
            setSecurityGroupToDelete(null);
            await loadSecurityGroups();
            setNotice('安全组已删除。');
        } catch (e) {
            setError((e as Error).message);
        } finally {
            setSaving(false);
        }
    }

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
            setSecurityGroups([]);
            setSecurityGroupsOpen(false);
            setAuditOpen(false);
            setAuditPage({ total: 0, events: [] });
            setNotice('已退出登录。');
        } catch (e) {
            setError((e as Error).message);
        }
    }
    async function openCreateClient() {
        try {
            const defaults = await api<AgentDefaults>('/api/v1/admin/agent-defaults');
            setClientDialog({ form: { ...emptyClient, agentServerHost: defaults.serverHost || '' } });
            setError('');
        } catch (e) {
            setError((e as Error).message);
        }
    }
    async function openCreateChannel(clientId: string) {
        try {
            const [suggestion] = await Promise.all([
                api<{ listenPort: number }>('/api/v1/admin/next-channel-port'),
                loadSecurityGroups(),
            ]);
            setChannelDialog({ clientId, form: { ...emptyChannel, listenPort: suggestion.listenPort } });
            setError('');
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
                          tags: parseTags(form.tags),
                      }
                    : { ...form, tags: parseTags(form.tags) },
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
    async function deleteClient() {
        if (!confirmDeleteClient) return;
        setSaving(true);
        setError('');
        try {
            await write(`/api/v1/admin/clients/${enc(confirmDeleteClient.clientId)}`, 'DELETE');
            setClients((current) =>
                current.filter((client) => client.clientId !== confirmDeleteClient.clientId),
            );
            setConfirmDeleteClient(null);
            setNotice('客户端及其通道、访问入口配置已删除。');
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
                original
                    ? { ...form, tags: parseTags(form.tags), channelId: undefined }
                    : { ...form, tags: parseTags(form.tags) },
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
            const { clientId, form } = mappingDialog;
            await write(`/api/v1/admin/clients/${enc(clientId)}/mappings`, 'POST', form);
            setMappingDialog(null);
            setNotice('端到端访问入口已创建并下发。创建后不可修改；如需调整，请删除后重建。');
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
            setNotice('端到端访问入口已删除并下发，原有连接已断开。');
            await refresh();
        } catch (e) {
            setError((e as Error).message);
        } finally {
            setSaving(false);
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
    const loadConnections = useCallback(async (clientId: string, channelId: string) => {
        try {
            const result = await api<{ connections: LiveConnection[] }>(
                `/api/v1/clients/${enc(clientId)}/channels/${enc(channelId)}/connections`,
            );
            setConnectionDialog((current) =>
                current?.client.clientId === clientId && current.channel.channelId === channelId
                    ? { ...current, connections: result.connections, loading: false }
                    : current,
            );
            setError('');
        } catch (e) {
            setConnectionDialog((current) =>
                current?.client.clientId === clientId && current.channel.channelId === channelId
                    ? { ...current, loading: false }
                    : current,
            );
            setError((e as Error).message);
        }
    }, []);
    function showConnections(client: ViewClient, channel: Channel) {
        setError('');
        setConfirmConnectionId(null);
        setConnectionDialog({ client, channel, connections: [], loading: true });
        void loadConnections(client.clientId, channel.channelId);
    }
    useEffect(() => {
        if (!connectionDialog) return;
        const clientId = connectionDialog.client.clientId;
        const channelId = connectionDialog.channel.channelId;
        const timer = window.setInterval(() => void loadConnections(clientId, channelId), 5000);
        return () => window.clearInterval(timer);
    }, [connectionDialog?.client.clientId, connectionDialog?.channel.channelId, loadConnections]);
    async function disconnectConnection() {
        if (!connectionDialog || !confirmConnectionId) return;
        setSaving(true);
        setError('');
        try {
            await write(
                `/api/v1/admin/clients/${enc(connectionDialog.client.clientId)}/channels/${enc(connectionDialog.channel.channelId)}/connections/${enc(confirmConnectionId)}`,
                'DELETE',
            );
            setConfirmConnectionId(null);
            await loadConnections(connectionDialog.client.clientId, connectionDialog.channel.channelId);
            await refresh();
            setNotice('已断开指定连接。');
        } catch (e) {
            setError((e as Error).message);
        } finally {
            setSaving(false);
        }
    }
    const clientTagTerms = parseTags(clientTagQuery);
    const channelTagTerms = parseTags(channelTagQuery);
    const visible = clients
        .filter(
            (c) =>
                (status === 'all' ||
                    (status === 'online' && c.online && c.enabled) ||
                    (status === 'offline' && !c.online && c.enabled) ||
                    (status === 'disabled' && !c.enabled)) &&
                `${c.clientId} ${c.displayName} ${c.channels.map((ch) => `${ch.channelId} ${ch.displayName}`).join(' ')}`
                    .toLowerCase()
                    .includes(query.toLowerCase()) &&
                hasAllTags(c.tags, clientTagTerms) &&
                (channelTagTerms.length === 0 ||
                    c.channels.some((ch) => hasAllTags(ch.tags, channelTagTerms))),
        )
        .map((client) =>
            channelTagTerms.length === 0
                ? client
                : {
                      ...client,
                      channels: client.channels.filter((channel) =>
                          hasAllTags(channel.tags, channelTagTerms),
                      ),
                  },
        );
    const stale = Boolean(error || (updatedAt && clock - updatedAt > 10000));

    return (
        <div className="shell">
            <header className="topbar">
                <div className="brand">
                    <img className="brand-mark" src="/relaylink-icon.svg" alt="" />
                    <span>
                        RelayLink <small>CONTROL CENTER</small>
                    </span>
                </div>
                <div className="top-actions">
                    <span className="live-label">
                        <span className={`live-dot${stale ? ' stale' : ''}`} />{' '}
                        {stale ? '数据已过期' : '实时监控'}
                    </span>
                    {session.authenticated && (
                        <button className="ghost" onClick={openAudit}>
                            <ClipboardList size={15} /> 审计日志
                        </button>
                    )}
                    {session.authenticated && (
                        <button className="ghost" onClick={() => void openSecurityGroups()}>
                            <Shield size={15} /> 安全组
                        </button>
                    )}
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
                                    ? `${fmt(overview.bytesToTarget + (overview.peerCiphertextToTarget ?? 0))} / ${fmt(overview.bytesToCaller + (overview.peerCiphertextToCaller ?? 0))}`
                                    : '—'}
                            </strong>
                            <small>
                                → 目标 / → 访问方 · 自 {overview ? time(overview.statsSinceUtc) : '—'}
                            </small>
                            <small title="互访通道的服务端转发字节；是否加密由目标通道设置决定">
                                互访转发：
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
                        <button className="primary" onClick={() => void openCreateClient()}>
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
                    <label className="tag-filter">
                        <span>客户端 tag</span>
                        <input
                            aria-label="客户端 tag 筛选"
                            placeholder="多个用逗号分隔"
                            value={clientTagQuery}
                            onChange={(e) => setClientTagQuery(e.target.value)}
                        />
                    </label>
                    <label className="tag-filter">
                        <span>通道 tag</span>
                        <input
                            aria-label="通道 tag 筛选"
                            placeholder="多个用逗号分隔"
                            value={channelTagQuery}
                            onChange={(e) => setChannelTagQuery(e.target.value)}
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
                                            {client.tags?.length > 0 && (
                                                <div className="tag-list" aria-label="客户端 tags">
                                                    {client.tags.map((tag) => (
                                                        <span className="tag" key={tag}>
                                                            {tag}
                                                        </span>
                                                    ))}
                                                </div>
                                            )}
                                            {session.authenticated && (
                                                <small
                                                    title={
                                                        client.e2eCertificateSha256 ||
                                                        'Agent 首次认证上线后自动登记'
                                                    }
                                                >
                                                    端到端证书：
                                                    {client.e2eCertificateSha256
                                                        ? `${client.e2eCertificateSha256.slice(0, 12)}…${client.e2eCertificateSha256.slice(-8)}`
                                                        : '待登记'}
                                                </small>
                                            )}
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
                                                            tags: (client.tags || []).join(', '),
                                                            agentServerHost: '',
                                                        },
                                                    })
                                                }
                                            >
                                                <Settings2 size={15} /> 编辑客户端
                                            </button>
                                            <button
                                                className="ghost danger-action"
                                                onClick={() => setConfirmDeleteClient(client)}
                                            >
                                                <Trash2 size={15} /> 删除客户端
                                            </button>
                                            <button
                                                className="ghost"
                                                onClick={() => void openCreateChannel(client.clientId)}
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
                                                <Plus size={15} /> 添加端到端访问入口
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
                                                            {ch.tags?.length > 0 && (
                                                                <div
                                                                    className="tag-list"
                                                                    aria-label="通道 tags"
                                                                >
                                                                    {ch.tags.map((tag) => (
                                                                        <span className="tag" key={tag}>
                                                                            {tag}
                                                                        </span>
                                                                    ))}
                                                                </div>
                                                            )}
                                                        </td>
                                                        <td className="mono">
                                                            {ch.authorizedClientsOnly
                                                                ? `仅客户端互访 · ${ch.endToEndEncryptionEnabled !== false ? '加密' : '明文'}`
                                                                : `${ch.listenAddress}:${ch.listenPort}`}
                                                            {ch.securityGroupId && (
                                                                <small>安全组：{ch.securityGroupId}</small>
                                                            )}
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
                                                            <button
                                                                className="flow"
                                                                title="查看此通道当前连接"
                                                                onClick={() => showConnections(client, ch)}
                                                            >
                                                                {ch.pendingConnections} /{' '}
                                                                {ch.activeConnections}
                                                            </button>
                                                        </td>
                                                        <td>
                                                            {ch.authorizedClientsOnly ? (
                                                                <span
                                                                    title={
                                                                        ch.endToEndEncryptionEnabled !== false
                                                                            ? '端到端 TLS 密文字节；服务端无法统计业务有效载荷'
                                                                            : '未加密的互访业务字节；服务端中继链路可读取内容'
                                                                    }
                                                                >
                                                                    {fmt(ch.peerCiphertextToTarget)} /{' '}
                                                                    {fmt(ch.peerCiphertextToCaller)}
                                                                </span>
                                                            ) : (
                                                                <button
                                                                    className="flow"
                                                                    title={
                                                                        ch.authorizedClientsOnly
                                                                            ? '查看此通道端到端密文历史流量'
                                                                            : '查看此通道历史流量'
                                                                    }
                                                                    onClick={() =>
                                                                        void showHistory(client, ch)
                                                                    }
                                                                >
                                                                    {fmt(
                                                                        ch.authorizedClientsOnly
                                                                            ? (ch.peerCiphertextToTarget ?? 0)
                                                                            : ch.bytesToTarget,
                                                                    )}{' '}
                                                                    /{' '}
                                                                    {fmt(
                                                                        ch.authorizedClientsOnly
                                                                            ? (ch.peerCiphertextToCaller ?? 0)
                                                                            : ch.bytesToCaller,
                                                                    )}
                                                                </button>
                                                            )}
                                                        </td>
                                                        {session.authenticated && (
                                                            <td>
                                                                <div className="row-actions">
                                                                    <button
                                                                        className="text-button"
                                                                        onClick={() => {
                                                                            void loadSecurityGroups().catch(
                                                                                (e) =>
                                                                                    setError(
                                                                                        (e as Error).message,
                                                                                    ),
                                                                            );
                                                                            setChannelDialog({
                                                                                clientId: client.clientId,
                                                                                original: ch,
                                                                                form: {
                                                                                    channelId: ch.channelId,
                                                                                    displayName:
                                                                                        ch.displayName,
                                                                                    tags: (
                                                                                        ch.tags || []
                                                                                    ).join(', '),
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
                                                                                    endToEndEncryptionEnabled:
                                                                                        ch.endToEndEncryptionEnabled !==
                                                                                        false,
                                                                                    securityGroupId:
                                                                                        ch.securityGroupId ||
                                                                                        '',
                                                                                },
                                                                            });
                                                                        }}
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
                                {client.mappings.length > 0 && (
                                    <section className="mapping-section" aria-label="端到端访问入口">
                                        <div className="mapping-heading">端到端访问入口</div>
                                        <div className="table-scroll">
                                            <table className="mapping-table">
                                                <thead>
                                                    <tr>
                                                        <th>入口 ID</th>
                                                        <th>本机地址</th>
                                                        <th>访问目标</th>
                                                        <th>状态</th>
                                                        {session.authenticated && (
                                                            <th className="action-col">操作</th>
                                                        )}
                                                    </tr>
                                                </thead>
                                                <tbody>
                                                    {client.mappings.map((mapping) => (
                                                        <tr key={mapping.mappingId}>
                                                            <td>
                                                                <strong
                                                                    className="mapping-id"
                                                                    title={mapping.mappingId}
                                                                >
                                                                    {mapping.mappingId}
                                                                </strong>
                                                            </td>
                                                            <td className="mono">
                                                                {mapping.available && mapping.localPort
                                                                    ? `${mapping.localAddress || '127.0.0.1'}:${mapping.localPort}`
                                                                    : '—'}
                                                            </td>
                                                            <td
                                                                className="mono"
                                                                title={`${mapping.targetClientId}/${mapping.targetChannelId}`}
                                                            >
                                                                {mapping.targetClientId}/
                                                                {mapping.targetChannelId}
                                                            </td>
                                                            <td>
                                                                <span
                                                                    className={`state ${!mapping.enabled ? 'muted' : mapping.available ? 'good' : 'warn'}`}
                                                                >
                                                                    <span />
                                                                    {!mapping.enabled
                                                                        ? '已禁用'
                                                                        : mapping.available
                                                                          ? '已监听'
                                                                          : '等待 Agent 上报'}
                                                                </span>
                                                            </td>
                                                            {session.authenticated && (
                                                                <td>
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
                                                                </td>
                                                            )}
                                                        </tr>
                                                    ))}
                                                </tbody>
                                            </table>
                                        </div>
                                    </section>
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
                title="审计日志"
                description="仅管理员可查看。记录登录、Agent 会话和连接生命周期元数据，不包含密码、密钥或业务内容。"
                open={auditOpen}
                onClose={() => setAuditOpen(false)}
                error={error}
                wide
            >
                <form
                    className="audit-filters"
                    onSubmit={(event) => {
                        event.preventDefault();
                        void loadAudit(1);
                    }}
                >
                    <label>
                        时间范围
                        <select
                            value={auditHours}
                            onChange={(event) => setAuditHours(Number(event.target.value))}
                        >
                            <option value={24}>最近 24 小时</option>
                            <option value={168}>最近 7 天</option>
                            <option value={720}>最近 30 天</option>
                            <option value={2160}>最近 90 天</option>
                        </select>
                    </label>
                    <label>
                        事件
                        <select value={auditType} onChange={(event) => setAuditType(event.target.value)}>
                            <option value="">全部事件</option>
                            {Object.entries(auditLabels).map(([value, label]) => (
                                <option key={value} value={value}>
                                    {label}
                                </option>
                            ))}
                        </select>
                    </label>
                    <label>
                        客户端 ID
                        <input
                            value={auditClient}
                            maxLength={128}
                            onChange={(event) => setAuditClient(event.target.value)}
                            placeholder="全部客户端"
                        />
                    </label>
                    <button className="primary" disabled={auditBusy}>
                        查询
                    </button>
                </form>
                <div className="audit-summary">
                    共 {auditPage.total} 条 · 第 {auditPageNumber} 页{' '}
                    <button
                        className="text-button"
                        disabled={auditBusy}
                        onClick={() => void loadAudit(auditPageNumber)}
                    >
                        刷新
                    </button>
                </div>
                <div className="table-scroll">
                    <table className="audit-table">
                        <thead>
                            <tr>
                                <th>时间</th>
                                <th>事件</th>
                                <th>结果 / 原因</th>
                                <th>客户端 / 通道</th>
                                <th>来源</th>
                                <th>连接 ID</th>
                                <th>流量 →目标 / →访问方</th>
                            </tr>
                        </thead>
                        <tbody>
                            {auditPage.events.map((item) => (
                                <tr key={item.eventId}>
                                    <td>{time(item.occurredAtUtc)}</td>
                                    <td>{auditLabels[item.eventType] || item.eventType}</td>
                                    <td
                                        title={`${item.outcome}${item.reasonCode ? ` · ${item.reasonCode}` : ''}`}
                                    >
                                        {item.outcome}
                                        {item.reasonCode ? ` · ${item.reasonCode}` : ''}
                                    </td>
                                    <td
                                        className="mono"
                                        title={`${item.clientId || item.actor || '—'}${item.channelId ? ` / ${item.channelId}` : ''}`}
                                    >
                                        {item.clientId || item.actor || '—'}
                                        {item.channelId ? ` / ${item.channelId}` : ''}
                                    </td>
                                    <td className="mono" title={item.remoteIp || item.callerClientId || '—'}>
                                        {item.remoteIp || item.callerClientId || '—'}
                                    </td>
                                    <td className="mono" title={item.connectionId || ''}>
                                        {item.connectionId ? `${item.connectionId.slice(0, 8)}…` : '—'}
                                    </td>
                                    <td className="mono">
                                        {fmt(item.bytesToTarget)} / {fmt(item.bytesToCaller)}
                                    </td>
                                </tr>
                            ))}
                            {!auditPage.events.length && (
                                <tr>
                                    <td className="empty-row" colSpan={7}>
                                        {auditBusy ? '加载中…' : '所选范围没有审计事件'}
                                    </td>
                                </tr>
                            )}
                        </tbody>
                    </table>
                </div>
                <div className="audit-pages">
                    <button
                        className="ghost"
                        disabled={auditBusy || auditPageNumber <= 1}
                        onClick={() => void loadAudit(auditPageNumber - 1)}
                    >
                        上一页
                    </button>
                    <button
                        className="ghost"
                        disabled={auditBusy || auditPageNumber * 50 >= auditPage.total}
                        onClick={() => void loadAudit(auditPageNumber + 1)}
                    >
                        下一页
                    </button>
                </div>
            </Modal>

            <Modal
                title="安全组"
                description="仅作用于普通通道的来源 IP；不设置安全组时允许任意来源。规则变更会立即撤销不再允许的连接。"
                open={securityGroupsOpen}
                onClose={() => {
                    setSecurityGroupsOpen(false);
                    setSecurityGroupForm(null);
                }}
                error={error}
            >
                <div className="security-group-actions">
                    <button
                        type="button"
                        className="primary subtle"
                        onClick={() => {
                            setSecurityGroupOriginalId(null);
                            setSecurityGroupForm({ id: '', name: '', entries: '' });
                        }}
                    >
                        <Plus size={15} /> 添加安全组
                    </button>
                </div>
                <div className="table-scroll">
                    <table className="security-group-table">
                        <thead>
                            <tr>
                                <th>名称 / ID</th>
                                <th>允许的 IP 或 IP 段</th>
                                <th>操作</th>
                            </tr>
                        </thead>
                        <tbody>
                            {securityGroups.map((group) => (
                                <tr key={group.id}>
                                    <td>
                                        <strong>{group.name}</strong>
                                        <small>{group.id}</small>
                                    </td>
                                    <td className="mono">{group.entries.join('、')}</td>
                                    <td>
                                        <div className="row-actions">
                                            <button
                                                type="button"
                                                className="text-button"
                                                onClick={() => {
                                                    setSecurityGroupOriginalId(group.id);
                                                    setSecurityGroupForm({
                                                        ...group,
                                                        entries: group.entries.join('\n'),
                                                    });
                                                }}
                                            >
                                                编辑
                                            </button>
                                            <button
                                                type="button"
                                                className="text-button danger"
                                                onClick={() => setSecurityGroupToDelete(group)}
                                            >
                                                删除
                                            </button>
                                        </div>
                                    </td>
                                </tr>
                            ))}
                            {securityGroups.length === 0 && (
                                <tr>
                                    <td colSpan={3} className="empty-row">
                                        暂无安全组
                                    </td>
                                </tr>
                            )}
                        </tbody>
                    </table>
                </div>
                {securityGroupForm && (
                    <form className="form security-group-form" onSubmit={saveSecurityGroup}>
                        <div className="form-grid">
                            <label className="field">
                                安全组 ID
                                <input
                                    required
                                    disabled={!!securityGroupOriginalId}
                                    pattern="[a-z0-9][a-z0-9_-]{0,63}"
                                    value={securityGroupForm.id}
                                    onChange={(e) =>
                                        setSecurityGroupForm({
                                            ...securityGroupForm,
                                            id: e.target.value.toLowerCase(),
                                        })
                                    }
                                />
                            </label>
                            <label className="field">
                                名称
                                <input
                                    required
                                    maxLength={100}
                                    value={securityGroupForm.name}
                                    onChange={(e) =>
                                        setSecurityGroupForm({ ...securityGroupForm, name: e.target.value })
                                    }
                                />
                            </label>
                        </div>
                        <label className="field">
                            允许的 IP 或 CIDR（每行一个）
                            <textarea
                                required
                                rows={5}
                                placeholder={'192.0.2.10\n198.51.100.0/24\n2001:db8::/32'}
                                value={securityGroupForm.entries}
                                onChange={(e) =>
                                    setSecurityGroupForm({ ...securityGroupForm, entries: e.target.value })
                                }
                            />
                        </label>
                        <div className="form-actions">
                            <button
                                type="button"
                                className="ghost"
                                onClick={() => setSecurityGroupForm(null)}
                            >
                                取消
                            </button>
                            <button className="primary" disabled={saving}>
                                保存安全组
                            </button>
                        </div>
                    </form>
                )}
            </Modal>
            <Modal
                title="删除安全组？"
                description="正在使用的安全组不能删除，请先从所有通道移除。"
                open={!!securityGroupToDelete}
                onClose={() => setSecurityGroupToDelete(null)}
                error={error}
            >
                <p>确定删除 {securityGroupToDelete?.name} 吗？</p>
                <div className="form-actions">
                    <button className="ghost" onClick={() => setSecurityGroupToDelete(null)}>
                        取消
                    </button>
                    <button className="primary" disabled={saving} onClick={() => void deleteSecurityGroup()}>
                        删除安全组
                    </button>
                </div>
            </Modal>

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
                                <span className="field-label">
                                    客户端 ID
                                    {!clientDialog.original && (
                                        <span
                                            className="field-help"
                                            title="输入大写字母会自动转为小写。"
                                            aria-label="输入大写字母会自动转为小写。"
                                            tabIndex={0}
                                        >
                                            <CircleAlert size={15} />
                                        </span>
                                    )}
                                </span>
                                <input
                                    required
                                    disabled={!!clientDialog.original}
                                    aria-label="客户端 ID"
                                    maxLength={64}
                                    pattern="[a-z0-9][a-z0-9_-]{0,63}"
                                    title="使用 1–64 位小写字母、数字、下划线或连字符，首位为字母或数字"
                                    value={clientDialog.form.clientId}
                                    onChange={(e) =>
                                        setClientDialog({
                                            ...clientDialog,
                                            form: {
                                                ...clientDialog.form,
                                                clientId: e.target.value.toLowerCase(),
                                            },
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
                            <label className="field full-width">
                                Tags
                                <input
                                    aria-label="客户端 Tags"
                                    maxLength={2100}
                                    placeholder="例如：生产, 上海, 数据库"
                                    value={clientDialog.form.tags}
                                    onChange={(e) =>
                                        setClientDialog({
                                            ...clientDialog,
                                            form: { ...clientDialog.form, tags: e.target.value },
                                        })
                                    }
                                />
                                <small>
                                    手动输入，多个 tag 使用逗号分隔；最多 32 个，每个不超过 64 个字符。
                                </small>
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
                                        <small>
                                            只填主机名或 IP，不含端口；端口由服务端配置。CA
                                            证书会自动写入下载的 Agent 配置。
                                        </small>
                                    </label>
                                </>
                            )}
                        </div>
                        {clientDialog.original && (
                            <label className="field">
                                端到端证书 SHA-256 指纹（Agent 首次认证后自动登记，只读）
                                <input
                                    readOnly
                                    value={clientDialog.original.e2eCertificateSha256 || '尚未登记'}
                                />
                            </label>
                        )}
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
                description="保存变更会更新监听、下发配置，并断开此通道的原有连接；相同内容重复保存不会断开。"
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
                            <label className="field full-width">
                                Tags
                                <input
                                    aria-label="通道 Tags"
                                    maxLength={2100}
                                    placeholder="例如：数据库, 只读"
                                    value={channelDialog.form.tags}
                                    onChange={(e) =>
                                        setChannelDialog({
                                            ...channelDialog,
                                            form: { ...channelDialog.form, tags: e.target.value },
                                        })
                                    }
                                />
                                <small>手动输入，多个 tag 使用逗号分隔；仅用于管理列表筛选。</small>
                            </label>
                            {!channelDialog.form.authorizedClientsOnly && (
                                <>
                                    <label className="field">
                                        云端监听地址
                                        <input
                                            required
                                            value={channelDialog.form.listenAddress}
                                            onChange={(e) =>
                                                setChannelDialog({
                                                    ...channelDialog,
                                                    form: {
                                                        ...channelDialog.form,
                                                        listenAddress: e.target.value,
                                                    },
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
                                        安全组
                                        <select
                                            value={channelDialog.form.securityGroupId}
                                            onChange={(e) =>
                                                setChannelDialog({
                                                    ...channelDialog,
                                                    form: {
                                                        ...channelDialog.form,
                                                        securityGroupId: e.target.value,
                                                    },
                                                })
                                            }
                                        >
                                            <option value="">不设置（允许任意来源）</option>
                                            {securityGroups.map((group) => (
                                                <option key={group.id} value={group.id}>
                                                    {group.name}（{group.id}）
                                                </option>
                                            ))}
                                        </select>
                                    </label>
                                </>
                            )}
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
                                            securityGroupId: e.target.checked
                                                ? ''
                                                : channelDialog.form.securityGroupId,
                                        },
                                    })
                                }
                            />{' '}
                            仅允许授权客户端互访（停止云端代理监听；服务端自动生成 32 字节访问密钥）
                        </label>
                        {channelDialog.form.authorizedClientsOnly && (
                            <label className="check">
                                <input
                                    type="checkbox"
                                    checked={channelDialog.form.endToEndEncryptionEnabled}
                                    onChange={(e) =>
                                        setChannelDialog({
                                            ...channelDialog,
                                            form: {
                                                ...channelDialog.form,
                                                endToEndEncryptionEnabled: e.target.checked,
                                            },
                                        })
                                    }
                                />{' '}
                                启用 Agent 间端到端加密（默认启用；关闭后业务流量以明文中继）
                            </label>
                        )}
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
                title="添加端到端访问入口"
                description="入口创建后不可修改；服务端下发访问配置，Agent 仅监听本机 127.0.0.1。"
                open={!!mappingDialog}
                onClose={() => setMappingDialog(null)}
                error={error}
            >
                {mappingDialog && (
                    <form className="form" onSubmit={saveMapping}>
                        <div className="form-grid">
                            <label className="field">
                                <span className="field-label">
                                    入口 ID（自动生成）
                                    <span
                                        className="field-help"
                                        title="入口 ID 为目标客户端 ID-目标通道 ID。本机端口由 Agent 自动选择并保存，冲突时自动轮换。"
                                        aria-label="入口 ID 为目标客户端 ID-目标通道 ID。本机端口由 Agent 自动选择并保存，冲突时自动轮换。"
                                        tabIndex={0}
                                    >
                                        <CircleAlert size={15} />
                                    </span>
                                </span>
                                <input
                                    aria-label="入口 ID（自动生成）"
                                    readOnly
                                    placeholder="选择目标后自动生成"
                                    value={
                                        mappingDialog.form.targetClientId &&
                                        mappingDialog.form.targetChannelId
                                            ? `${mappingDialog.form.targetClientId}-${mappingDialog.form.targetChannelId}`
                                            : ''
                                    }
                                />
                            </label>
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
                title="删除端到端访问入口？"
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
                title={`${connectionDialog?.client.displayName ?? ''} / ${connectionDialog?.channel.displayName ?? ''} 当前连接`}
                description="建连中与转发中的实时快照，每 5 秒更新。断开连接会中止当前业务会话。"
                open={!!connectionDialog}
                onClose={() => {
                    setConnectionDialog(null);
                    setConfirmConnectionId(null);
                }}
                error={error}
            >
                {connectionDialog && (
                    <>
                        <div className="history-toolbar">
                            <span>当前 {connectionDialog.connections.length} 条连接</span>
                            <button
                                className="ghost"
                                onClick={() =>
                                    void loadConnections(
                                        connectionDialog.client.clientId,
                                        connectionDialog.channel.channelId,
                                    )
                                }
                            >
                                刷新
                            </button>
                        </div>
                        <div className="table-scroll">
                            <table className="connection-table">
                                <thead>
                                    <tr>
                                        <th>连接 ID</th>
                                        <th>来源</th>
                                        <th>状态</th>
                                        <th>建立时间</th>
                                        <th>流量 →目标 / →访问方</th>
                                        {session.authenticated && <th>操作</th>}
                                    </tr>
                                </thead>
                                <tbody>
                                    {connectionDialog.connections.map((connection) => (
                                        <tr key={connection.connectionId}>
                                            <td className="mono" title={connection.connectionId}>
                                                {connection.connectionId.slice(0, 8)}…
                                            </td>
                                            <td className="mono">{connection.source}</td>
                                            <td>
                                                {connection.state === 'relaying' ? '转发中' : '建连中'}
                                                {connection.kind === 'end-to-end' ? ' · 端到端' : ''}
                                            </td>
                                            <td>{time(connection.startedAtUtc)}</td>
                                            <td
                                                className="mono"
                                                title={
                                                    connection.kind === 'end-to-end'
                                                        ? '端到端密文传输字节'
                                                        : '普通代理传输字节'
                                                }
                                            >
                                                {fmt(connection.bytesToTarget)} /{' '}
                                                {fmt(connection.bytesToCaller)}
                                            </td>
                                            {session.authenticated && (
                                                <td>
                                                    {confirmConnectionId === connection.connectionId ? (
                                                        <>
                                                            <button
                                                                className="text-button danger"
                                                                disabled={saving}
                                                                onClick={() => void disconnectConnection()}
                                                            >
                                                                确认断开
                                                            </button>{' '}
                                                            <button
                                                                className="text-button"
                                                                onClick={() => setConfirmConnectionId(null)}
                                                            >
                                                                取消
                                                            </button>
                                                        </>
                                                    ) : (
                                                        <button
                                                            className="text-button danger"
                                                            onClick={() =>
                                                                setConfirmConnectionId(
                                                                    connection.connectionId,
                                                                )
                                                            }
                                                        >
                                                            断开
                                                        </button>
                                                    )}
                                                </td>
                                            )}
                                        </tr>
                                    ))}
                                    {!connectionDialog.connections.length && (
                                        <tr>
                                            <td className="empty-row" colSpan={session.authenticated ? 6 : 5}>
                                                {connectionDialog.loading ? '加载中…' : '当前没有连接'}
                                            </td>
                                        </tr>
                                    )}
                                </tbody>
                            </table>
                        </div>
                    </>
                )}
            </Modal>

            <Modal
                title={`${historyDialog?.client.displayName ?? ''} / ${historyDialog?.channel.displayName ?? ''}`}
                description="历史流量按分钟存储；图表随时间范围按分钟、15 分钟或小时汇总。互访通道统计服务端转发字节，加密通道对应密文字节。"
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
                            所选时段合计　→ 目标{' '}
                            <b>
                                {fmt(
                                    historyDialog.samples.reduce(
                                        (sum, sample) => sum + sample.bytesToTarget,
                                        0,
                                    ),
                                )}
                            </b>
                            　→ 访问方{' '}
                            <b>
                                {fmt(
                                    historyDialog.samples.reduce(
                                        (sum, sample) => sum + sample.bytesToCaller,
                                        0,
                                    ),
                                )}
                            </b>
                        </div>
                    </div>
                )}
            </Modal>

            <Modal
                title="删除客户端？"
                description="删除后将停止该客户端的监听与会话，并删除服务端客户端配置文件。"
                open={!!confirmDeleteClient}
                onClose={() => setConfirmDeleteClient(null)}
                error={error}
            >
                <p>
                    确定删除客户端「{confirmDeleteClient?.displayName}
                    」及其全部通道和访问入口？此操作不可撤销。
                </p>
                <div className="form-actions">
                    <button className="ghost" onClick={() => setConfirmDeleteClient(null)}>
                        取消
                    </button>
                    <button
                        className="primary danger-fill"
                        disabled={saving}
                        onClick={() => void deleteClient()}
                    >
                        删除客户端
                    </button>
                </div>
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
