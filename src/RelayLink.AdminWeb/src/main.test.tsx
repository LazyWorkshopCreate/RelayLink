import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { App } from './main';

const ok = (body: unknown, status = 200) =>
    new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });

function fakeApi() {
    let authenticated = false;
    const clients: Record<string, unknown>[] = [
        {
            clientId: 'node-a',
            displayName: '测试节点',
            enabled: true,
            online: true,
            maxConnections: 10,
            maxPendingConnections: 5,
        },
    ];
    const channelSets: Record<string, Record<string, unknown>[]> = {
        'node-a': [
            {
                channelId: 'echo',
                displayName: '回显',
                enabled: true,
                available: true,
                listenAddress: '127.0.0.1',
                listenPort: 19000,
                targetHost: '127.0.0.1',
                targetPort: 19001,
                maxConnections: 5,
                targetConnectTimeoutSeconds: 5,
                pendingConnections: 0,
                activeConnections: 0,
                bytesToTarget: 4096,
                bytesToCaller: 4100,
            },
        ],
        'node-b': [],
    };
    const mappingSets: Record<string, Record<string, unknown>[]> = { 'node-a': [], 'node-b': [] };
    const calls: { path: string; method: string; csrf: string | null; body?: unknown }[] = [];
    const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
        const path = String(input);
        const method = init?.method || 'GET';
        const headers = new Headers(init?.headers);
        const body = init?.body ? JSON.parse(String(init.body)) : undefined;
        calls.push({ path, method, csrf: headers.get('X-RelayLink-CSRF'), body });
        if (path === '/api/v1/admin/session' && method === 'POST') {
            authenticated = true;
            return ok({ authenticated: true, csrfToken: 'test-csrf' });
        }
        if (path === '/api/v1/admin/session')
            return ok({ authenticated, csrfToken: authenticated ? 'test-csrf' : undefined });
        if (path === '/api/v1/overview')
            return ok({
                statsSinceUtc: '2026-09-16T10:00:00Z',
                snapshotTimeUtc: '2026-09-16T10:01:00Z',
                clientsOnline: 1,
                clientsTotal: clients.length,
                channelsAvailable: channelSets['node-a'].length + channelSets['node-b'].length,
                channelsTotal: channelSets['node-a'].length + channelSets['node-b'].length,
                activeConnections: 0,
                bytesToTarget: 4096,
                bytesToCaller: 4100,
            });
        if (path.startsWith('/api/v1/clients?page=')) return ok({ clients, total: clients.length });
        if (path.endsWith('/channels') && method === 'GET') {
            const clientId = path.split('/')[4];
            return ok({ channels: channelSets[clientId] || [] });
        }
        if (path.endsWith('/mappings') && method === 'GET')
            return ok({ mappings: mappingSets[path.split('/')[5]] || [] });
        if (path.endsWith('/identity') && method === 'GET')
            return ok({ e2eCertificateSha256: 'A'.repeat(64) });
        if (path.startsWith('/api/v1/history'))
            return ok({
                samples: [
                    { timestampUtc: '2026-09-16T10:00:00Z', bytesToTarget: 100, bytesToCaller: 110 },
                    { timestampUtc: '2026-09-16T10:01:00Z', bytesToTarget: 4096, bytesToCaller: 4100 },
                ],
            });
        if (path === '/api/v1/admin/clients' && method === 'POST') {
            clients.push({ ...(body as object), online: false });
            return ok({ clientId: (body as { clientId: string }).clientId }, 201);
        }
        if (/^\/api\/v1\/admin\/clients\/[^/]+$/.test(path) && method === 'PUT') {
            const client = clients.find((c) => c.clientId === path.split('/')[5]);
            Object.assign(client!, body);
            return ok(client);
        }
        if (path.endsWith('/channels') && method === 'POST') {
            const clientId = path.split('/')[5];
            (channelSets[clientId] ||= []).push({
                ...(body as object),
                available: false,
                pendingConnections: 0,
                activeConnections: 0,
                bytesToTarget: 0,
                bytesToCaller: 0,
            });
            return ok({ message: '已下发', channel: body }, 201);
        }
        if (path.endsWith('/mappings') && method === 'POST') {
            const clientId = path.split('/')[5];
            (mappingSets[clientId] ||= []).push({
                ...(body as object),
                localAddress: '127.0.0.1',
                localPort: 23456,
                available: true,
            });
            return ok({ mapping: body }, 201);
        }
        if (/\/channels\/[^/]+$/.test(path) && method === 'PUT') {
            const channel = channelSets[path.split('/')[5]].find((c) => c.channelId === path.split('/')[7]);
            Object.assign(channel!, body);
            return ok({ message: '已下发', channel });
        }
        if (/\/channels\/[^/]+$/.test(path) && method === 'DELETE') {
            const set = channelSets[path.split('/')[5]];
            const index = set.findIndex((c) => c.channelId === path.split('/')[7]);
            set.splice(index, 1);
            return ok({ message: '已删除', pushedToAgent: true });
        }
        return ok({ error: `Unexpected ${method} ${path}` }, 500);
    });
    vi.stubGlobal('fetch', fetchMock);
    return calls;
}

afterEach(() => {
    cleanup();
    vi.unstubAllGlobals();
    vi.clearAllMocks();
});

describe('管理控制台', () => {
    it('匿名只读并可查看按通道聚合的历史图表', async () => {
        fakeApi();
        const user = userEvent.setup();
        render(<App />);
        expect(await screen.findByText('测试节点')).toBeTruthy();
        expect(screen.getByRole('button', { name: /未登录/ })).toBeTruthy();
        expect(screen.queryByRole('button', { name: '添加客户端' })).toBeNull();
        await user.click(screen.getByRole('button', { name: /4\.0 KiB \/ 4\.0 KiB/ }));
        expect(await screen.findByRole('img', { name: '目标和访问方历史累计流量折线图' })).toBeTruthy();
        expect(screen.getByText('最近 24 小时')).toBeTruthy();
    });

    it('登录后创建客户端和通道时携带 CSRF，保存后刷新列表', async () => {
        const calls = fakeApi();
        const user = userEvent.setup();
        render(<App />);
        await screen.findByText('测试节点');
        await user.click(screen.getByRole('button', { name: /未登录/ }));
        await user.type(screen.getByRole('textbox', { name: '用户名' }), 'admin');
        await user.type(screen.getByLabelText('密码'), 'test-password');
        await user.click(screen.getByRole('button', { name: '登录' }));
        await user.click(await screen.findByRole('button', { name: '添加客户端' }));
        await user.type(screen.getByRole('textbox', { name: '客户端 ID' }), 'node-b');
        await user.type(screen.getByRole('textbox', { name: '显示名称' }), '新节点');
        await user.type(screen.getByRole('textbox', { name: /Agent 连接的服务端主机/ }), '127.0.0.1');
        await user.click(screen.getByRole('button', { name: '保存客户端' }));
        await screen.findByText('新节点');
        expect(calls.find((c) => c.path === '/api/v1/admin/clients' && c.method === 'POST')?.csrf).toBe(
            'test-csrf',
        );

        await user.click(screen.getAllByRole('button', { name: '添加通道' })[0]);
        await user.type(screen.getByRole('textbox', { name: '通道 ID' }), 'new-channel');
        await user.type(screen.getByRole('textbox', { name: '显示名称' }), '新通道');
        await user.click(screen.getByRole('button', { name: '保存并下发' }));
        await screen.findByText('新通道');
        expect(calls.find((c) => c.path.endsWith('/channels') && c.method === 'POST')?.csrf).toBe(
            'test-csrf',
        );
        await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());

        const row = screen.getByText('新通道').closest('tr')!;
        await user.click(within(row).getByRole('button', { name: '编辑' }));
        await user.click(screen.getByRole('checkbox', { name: '启用通道' }));
        await user.click(screen.getByRole('button', { name: '保存并下发' }));
        await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
        expect(calls.find((c) => c.path.endsWith('/channels/new-channel') && c.method === 'PUT')?.csrf).toBe(
            'test-csrf',
        );
        expect(within(screen.getByText('新通道').closest('tr')!).getByText('已禁用')).toBeTruthy();

        await user.click(
            within(screen.getByText('新通道').closest('tr')!).getByRole('button', { name: '删除' }),
        );
        await user.click(screen.getByRole('button', { name: '删除通道' }));
        await waitFor(() => expect(screen.queryByText('新通道')).toBeNull());
        expect(
            calls.find((c) => c.path.endsWith('/channels/new-channel') && c.method === 'DELETE')?.csrf,
        ).toBe('test-csrf');
    });

    it('授权通道和本机互访入口由管理员配置并下发', async () => {
        const calls = fakeApi();
        const user = userEvent.setup();
        render(<App />);
        await screen.findByText('测试节点');
        await user.click(screen.getByRole('button', { name: /未登录/ }));
        await user.type(screen.getByRole('textbox', { name: '用户名' }), 'admin');
        await user.type(screen.getByLabelText('密码'), 'test-password');
        await user.click(screen.getByRole('button', { name: '登录' }));
        await user.click(await screen.findByRole('button', { name: '添加客户端' }));
        await user.type(screen.getByRole('textbox', { name: '客户端 ID' }), 'node-b');
        await user.type(screen.getByRole('textbox', { name: '显示名称' }), '新节点');
        await user.type(screen.getByRole('textbox', { name: /Agent 连接的服务端主机/ }), '127.0.0.1');
        await user.click(screen.getByRole('button', { name: '保存客户端' }));
        await screen.findByText('新节点');

        const targetCard = screen.getByText('测试节点').closest('article')!;
        await user.click(within(targetCard).getByRole('button', { name: '添加通道' }));
        await user.type(screen.getByRole('textbox', { name: '通道 ID' }), 'private');
        await user.type(screen.getByRole('textbox', { name: '显示名称' }), '私有通道');
        await user.click(screen.getByRole('checkbox', { name: /仅允许授权客户端互访/ }));
        await user.click(screen.getByRole('button', { name: '读取当前在线 Agent 指纹' }));
        await waitFor(() =>
            expect(screen.getByRole('textbox', { name: /端到端证书 SHA-256 指纹/ })).toHaveProperty(
                'value',
                'A'.repeat(64),
            ),
        );
        await user.click(screen.getByRole('button', { name: '保存并下发' }));
        await screen.findByText('私有通道');
        expect(calls.find((c) => c.path.endsWith('/channels') && c.method === 'POST')?.csrf).toBe(
            'test-csrf',
        );

        const callerCard = screen.getByText('新节点').closest('article')!;
        await user.click(within(callerCard).getByRole('button', { name: '添加互访入口' }));
        await user.type(screen.getByRole('textbox', { name: '映射 ID' }), 'to-private');
        await user.selectOptions(screen.getByRole('combobox', { name: '目标客户端' }), 'node-a');
        await user.selectOptions(screen.getByRole('combobox', { name: '目标授权通道' }), 'private');
        await user.click(screen.getByRole('button', { name: '保存并下发' }));
        await screen.findByText(/to-private · 127\.0\.0\.1:/);
        expect(calls.find((c) => c.path.endsWith('/mappings') && c.method === 'POST')?.csrf).toBe(
            'test-csrf',
        );
        expect(
            calls.find((c) => c.path.endsWith('/mappings') && c.method === 'POST')?.body,
        ).not.toHaveProperty('localPort');
    });
});
