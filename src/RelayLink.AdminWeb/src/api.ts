export const fmt = (n: number) =>
    n < 1024
        ? `${n || 0} B`
        : n < 1048576
          ? `${(n / 1024).toFixed(1)} KiB`
          : `${(n / 1048576).toFixed(1)} MiB`;

export const time = (s?: string | null) => (s ? new Date(s).toLocaleString('zh-CN') : '—');
export const enc = encodeURIComponent;

export const parseTags = (value: string) => [
    ...new Map(
        value
            .split(/[,，\n]+/)
            .map((tag) => tag.trim())
            .filter(Boolean)
            .map((tag) => [tag.toLowerCase(), tag]),
    ).values(),
];

export const hasAllTags = (tags: string[] | undefined, required: string[]) => {
    const normalized = new Set((tags || []).map((tag) => tag.toLowerCase()));
    return required.every((tag) => normalized.has(tag.toLowerCase()));
};

const operationErrors: Record<string, string> = {
    'Agent-to-Agent authorization requires tunnel TLS to protect access-secret distribution.':
        '授权互访需要控制连接 TLS 保护访问密钥下发。请先在服务端启用控制 TLS。',
    'HTTP requires control TLS.': 'HTTP 代理要求服务端启用控制连接 TLS。',
    'Target client has not registered an E2E certificate yet.':
        '目标客户端尚未登记证书指纹，请先启动目标 Agent 完成首次上线。',
    'Target authorized channel was not found.': '未找到启用的目标授权通道，请刷新并检查目标配置。',
    'Target client ID and channel ID are required.': '请选择目标客户端和授权通道。',
    'Generated access entry ID already exists.': '该目标已有互访入口，请检查现有入口。',
    'Channel ID already exists.': '通道 ID 已存在，请更换 ID。',
    'Channel was not found.': '通道已不存在，请刷新页面。',
    'Client was not found.': '客户端已不存在，请刷新页面。',
    'Mapping was not found.': '互访入口已不存在，请刷新页面。',
    'History storage is unavailable.': '历史流量存储不可用，请检查服务端存储。',
    'A valid Agent server host (without a port) is required.':
        '请输入 Agent 可达的服务端主机名或 IP，端口另行填写。',
    'Configure tunnel.trustedCaPemPath on the server before creating clients.':
        '请先在服务端配置 tunnel.trustedCaPemPath，再创建客户端。',
};
async function readErrorMessage(response: Response): Promise<string> {
    const body = (await response.json().catch(() => ({}))) as {
        error?: string;
        detail?: string;
        message?: string;
        title?: string;
    };
    const message = body.error || body.detail || body.message || body.title;
    return message ? (operationErrors[message] ?? message) : `请求失败（HTTP ${response.status}）`;
}

export async function api<T>(path: string, init?: RequestInit): Promise<T> {
    const response = await fetch(path, { credentials: 'same-origin', cache: 'no-store', ...init });
    if (!response.ok) {
        throw new Error(await readErrorMessage(response));
    }
    return response.status === 204 ? (undefined as T) : (response.json() as Promise<T>);
}

function parseDownloadFilename(header: string | null, fallback: string): string {
    if (!header) return fallback;
    const utf8Match = /filename\*=UTF-8''([^;]+)/i.exec(header);
    if (utf8Match?.[1]) {
        try {
            return decodeURIComponent(utf8Match[1].trim());
        } catch {
            return fallback;
        }
    }
    const plainMatch = /filename="?([^";]+)"?/i.exec(header);
    return plainMatch?.[1]?.trim() || fallback;
}

export async function downloadAgentConfig(clientId: string): Promise<void> {
    const response = await fetch(`/api/v1/admin/clients/${enc(clientId)}/agent-config`, {
        credentials: 'same-origin',
        cache: 'no-store',
    });
    if (!response.ok) {
        throw new Error(await readErrorMessage(response));
    }
    const blob = await response.blob();
    const url = URL.createObjectURL(blob);
    const link = document.createElement('a');
    link.href = url;
    link.download = parseDownloadFilename(
        response.headers.get('Content-Disposition'),
        `relaylink-agent-${clientId}.json`,
    );
    document.body.append(link);
    link.click();
    link.remove();
    window.setTimeout(() => URL.revokeObjectURL(url), 1000);
}
