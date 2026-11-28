export type Session = {
    authenticated: boolean;
    csrfToken?: string;
    expiresAtUtc?: string;
    automationEnabled?: boolean;
    automationSessionLifetimeMinutes?: number | null;
};

export type AgentDefaults = {
    serverHost: string | null;
    serverPort: number;
    useTls: boolean;
};

export type Overview = {
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
    tcpIdleTimeoutSeconds?: number;
    halfCloseDrainTimeoutSeconds?: number;
};

export type Client = {
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

export type Channel = {
    channelId: string;
    displayName: string;
    tags: string[];
    enabled: boolean;
    authorizedClientsOnly?: boolean;
    endToEndEncryptionEnabled?: boolean;
    securityGroupId?: string | null;
    available: boolean;
    peerState?: string | null;
    listenAddress: string;
    listenPort: number;
    targetHost: string;
    targetPort: number;
    maxConnections: number;
    targetConnectTimeoutSeconds: number;
    applicationProtocol?: 'raw' | 'http';
    httpState?: string | null;
    entryScheme?: 'http' | 'https' | null;
    accessUrl?: string | null;
    transportProtocol?: 'tcp' | 'udp';
    udpIdleTimeoutSeconds?: number;
    pendingConnections: number;
    activeConnections: number;
    bytesToTarget: number;
    bytesToCaller: number;
    peerCiphertextToTarget: number;
    peerCiphertextToCaller: number;
    targetLastResult?: string;
};

export type Mapping = {
    endToEndEncryptionEnabled?: boolean | null;
    applicationProtocol?: 'raw' | 'http';
    httpState?: string | null;
    entryScheme?: 'http' | 'https' | null;
    accessUrl?: string | null;
    transportProtocol?: 'tcp' | 'udp';
    mappingId: string;
    enabled: boolean;
    localAddress: string | null;
    localPort: number | null;
    available: boolean;
    targetClientId: string;
    targetChannelId: string;
};

export type SecurityGroup = { id: string; name: string; entries: string[] };
export type SecurityGroupForm = { id: string; name: string; entries: string };
export type HistorySample = { timestampUtc: string; bytesToTarget: number; bytesToCaller: number };
export type LiveConnection = {
    endToEndEncryptionEnabled?: boolean | null;
    connectionId: string;
    kind: string;
    state: string;
    source: string;
    startedAtUtc: string;
    bytesToTarget: number;
    bytesToCaller: number;
};

export type AuditRow = {
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

export type AuditPage = { total: number; events: AuditRow[] };

// 自动化管理面：待批准配对、已签发授权、变更申请。
// 这里展示的 scope 与设备指纹都是非秘密元数据。
export type AutomationPairing = {
    requestId: string;
    subject: string;
    platform: string;
    cliVersion: string;
    scopes: string[];
    deviceKeyThumbprint: string;
    userCode: string;
    state: string;
    createdAtUtc: string;
    expiresAtUtc: string;
    decidedBy?: string | null;
};

export type AutomationGrant = {
    grantId: string;
    subject: string;
    platform: string;
    deviceKeyThumbprint: string;
    scopes: string[];
    createdAtUtc: string;
    expiresAtUtc: string;
    revokedAtUtc?: string | null;
};

export type AutomationChange = {
    changeRequestId: string;
    type: string;
    targetKey: string;
    payload: string;
    reason?: string | null;
    requestedBy: string;
    state: string;
    createdAtUtc: string;
    expiresAtUtc: string;
    failureReason?: string | null;
};

export const auditLabels: Record<string, string> = {
    admin_login_success: '管理员登录成功',
    admin_login_failure: '管理员登录失败',
    admin_logout: '管理员退出',
    agent_session_ready: 'Agent 上线',
    agent_session_closed: 'Agent 离线',
    agent_session_rejected: 'Agent 认证拒绝',
    proxy_connection_opened: '普通连接建立',
    proxy_connection_closed: '普通连接断开',
    proxy_connection_rejected: '普通连接拒绝',
    peer_connection_opened: '互访连接建立',
    peer_connection_closed: '互访连接断开',
    peer_connection_rejected: '互访连接拒绝',
    automation_pairing_requested: '自动化配对申请',
    automation_pairing_approved: '自动化配对批准',
    automation_pairing_denied: '自动化配对拒绝',
    automation_pairing_locked: '自动化配对锁定',
    automation_token_issued: '自动化令牌签发',
    automation_token_revoked: '自动化授权撤销',
    automation_request: '自动化只读访问',
    automation_disabled: '自动化未启用访问',
    automation_change_requested: '自动化变更提交',
    automation_change_approved: '自动化变更批准',
    automation_change_rejected: '自动化变更拒绝',
    automation_change_cancelled: '自动化变更撤销',
    automation_change_applying: '自动化变更执行中',
    automation_change_applied: '自动化变更已生效',
    automation_change_failed: '自动化变更失败',
};

export type ViewClient = Client & { channels: Channel[]; mappings: Mapping[] };
export type DashboardSnapshot = {
    snapshotTimeUtc: string;
    page: number;
    pageSize: number;
    total: number;
    overview: Overview;
    clients: ViewClient[];
};

export type ClientForm = {
    clientId: string;
    displayName: string;
    tags: string;
    enabled: boolean;
    maxConnections: number;
    maxPendingConnections: number;
    agentServerHost: string;
};

export type ChannelForm = {
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
    transportProtocol?: 'tcp' | 'udp';
    udpIdleTimeoutSeconds?: number;
    authorizedClientsOnly: boolean;
    endToEndEncryptionEnabled: boolean;
    securityGroupId: string;
};

export type MappingForm = {
    targetClientId: string;
    targetChannelId: string;
};

export const emptyClient: ClientForm = {
    clientId: '',
    displayName: '',
    tags: '',
    enabled: true,
    maxConnections: 100,
    maxPendingConnections: 100,
    agentServerHost: '',
};

export const emptyChannel: ChannelForm = {
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
    transportProtocol: 'tcp',
    udpIdleTimeoutSeconds: 60,
    authorizedClientsOnly: false,
    endToEndEncryptionEnabled: true,
    securityGroupId: '',
};

export const emptyMapping: MappingForm = {
    targetClientId: '',
    targetChannelId: '',
};
