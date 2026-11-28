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

async function readErrorMessage(response: Response): Promise<string> {
    const body = (await response.json().catch(() => ({}))) as {
        error?: string;
        detail?: string;
        message?: string;
        title?: string;
    };
    return (
        body.error || body.detail || body.message || body.title || `${response.status} ${response.statusText}`
    );
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
