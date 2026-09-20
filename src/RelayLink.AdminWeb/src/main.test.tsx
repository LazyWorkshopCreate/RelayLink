import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { App } from './main';

const ok = (body: unknown, status = 200) =>
    new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });

function fakeApi(withMapping = false) {
    let authenticated = false;
    const clients: Record<string, unknown>[] = [
        {
            clientId: 'node-a',
            displayName: '测试节点',
            enabled: true,
            online: true,
            e2eCertificateSha256: 'A'.repeat(64),
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
    const securityGroups: { id: string; name: string; entries: string[] }[] = [];
    const connections = [
        {
            connectionId: '11111111-1111-1111-1111-111111111111',
            kind: 'proxy',
            state: 'relaying',
            source: '127.0.0.1:50000',
            startedAtUtc: '2026-09-16T10:00:00Z',
            bytesToTarget: 1024,
            bytesToCaller: 2048,
        },
    ];
    if (withMapping) {
        clients.push({
            clientId: 'node-b',
            displayName: '访问节点',
            enabled: true,
            online: true,
            maxConnections: 10,
            maxPendingConnections: 5,
        });
        mappingSets['node-b'].push({
            mappingId: 'node-a-echo',
            enabled: true,
            localAddress: '127.0.0.1',
            localPort: 23456,
            available: true,
            targetClientId: 'node-a',
            targetChannelId: 'echo',
        });
    }
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
        if (path === '/api/v1/admin/agent-defaults')
            return ok({ serverHost: 'tunnel.example.com', serverPort: 7443, useTls: true });
        if (path.startsWith('/api/v1/admin/audit?'))
            return authenticated
                ? ok({
                      total: 1,
                      events: [
                          {
                              eventId: 'e1',
                              occurredAtUtc: '2026-09-16T10:01:00Z',
                              eventType: 'admin_login_success',
                              outcome: 'success',
                              actor: 'admin',
                              bytesToTarget: 0,
                              bytesToCaller: 0,
                          },
                      ],
                  })
                : ok({ error: 'Unauthorized' }, 401);
        if (path === '/api/v1/admin/security-groups' && method === 'GET') return ok({ securityGroups });
        if (path === '/api/v1/admin/security-groups' && method === 'POST') {
            securityGroups.push(body as { id: string; name: string; entries: string[] });
            return ok(body, 201);
        }
        if (path.startsWith('/api/v1/admin/security-groups/') && method === 'PUT') {
            const group = securityGroups.find((item) => item.id === path.split('/')[5]);
            Object.assign(group!, body);
            return ok(group);
        }
        if (path.startsWith('/api/v1/admin/security-groups/') && method === 'DELETE') {
            securityGroups.splice(
                securityGroups.findIndex((item) => item.id === path.split('/')[5]),
                1,
            );
            return new Response(null, { status: 204 });
        }
        if (path === '/api/v1/admin/next-channel-port') {
            const used = new Set(
                Object.values(channelSets)
                    .flat()
                    .filter((channel) => !channel.authorizedClientsOnly)
                    .map((channel) => channel.listenPort),
            );
            let listenPort = 19000;
            while (used.has(listenPort)) listenPort++;
            return ok({ listenPort });
        }
        if (path.startsWith('/api/v1/dashboard/snapshot?'))
            return ok({
                snapshotTimeUtc: '2026-09-16T10:01:00Z',
                page: 1,
                pageSize: 100,
                total: clients.length,
                overview: {
                    statsSinceUtc: '2026-09-16T10:00:00Z',
                    snapshotTimeUtc: '2026-09-16T10:01:00Z',
                    clientsOnline: 1,
                    clientsTotal: clients.length,
                    channelsAvailable: channelSets['node-a'].length + channelSets['node-b'].length,
                    channelsTotal: channelSets['node-a'].length + channelSets['node-b'].length,
                    activeConnections: 0,
                    bytesToTarget: 4096,
                    bytesToCaller: 4100,
                    peerCiphertextToTarget: 0,
                    peerCiphertextToCaller: 0,
                },
                clients: clients.map((client) => ({
                    ...client,
                    channels: channelSets[String(client.clientId)] || [],
                    mappings: mappingSets[String(client.clientId)] || [],
                })),
            });
        if (path.endsWith('/channels') && method === 'GET') {
            const clientId = path.split('/')[4];
            return ok({ channels: channelSets[clientId] || [] });
        }
        if (path === '/api/v1/clients/node-a/channels/echo/connections' && method === 'GET')
            return ok({ connections });
        if (
            path ===
                '/api/v1/admin/clients/node-a/channels/echo/connections/11111111-1111-1111-1111-111111111111' &&
            method === 'DELETE'
        ) {
            connections.splice(0);
            return new Response(null, { status: 204 });
        }
        if (path.startsWith('/api/v1/clients/') && path.endsWith('/mappings') && method === 'GET')
            return ok({ mappings: mappingSets[path.split('/')[4]] || [] });
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
            const mapping = body as { targetClientId: string; targetChannelId: string };
            (mappingSets[clientId] ||= []).push({
                ...mapping,
                mappingId: `${mapping.targetClientId}-${mapping.targetChannelId}`,
                enabled: true,
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
    it('审计日志仅登录后可查看并支持筛选查询', async () => {
        const calls = fakeApi();
        const user = userEvent.setup();
        render(<App />);
        await screen.findByText('测试节点');
        expect(screen.queryByRole('button', { name: '审计日志' })).toBeNull();
        await user.click(screen.getByRole('button', { name: /未登录/ }));
        await user.type(screen.getByRole('textbox', { name: '用户名' }), 'admin');
        await user.type(screen.getByLabelText('密码'), 'test-password');
        await user.click(screen.getByRole('button', { name: '登录' }));
        await user.click(await screen.findByRole('button', { name: '审计日志' }));
        expect(await screen.findByRole('cell', { name: '管理员登录成功' })).toBeTruthy();
        await user.selectOptions(screen.getByRole('combobox', { name: '时间范围' }), '168');
        await user.type(screen.getByRole('textbox', { name: '客户端 ID' }), 'node-a');
        await user.click(screen.getByRole('button', { name: '查询' }));
        expect(
            calls.some(
                (call) =>
                    call.path.includes('/api/v1/admin/audit?hours=168') &&
                    call.path.includes('clientId=node-a'),
            ),
        ).toBe(true);
    });

    it('匿名可查看当前连接，登录后可带 CSRF 断开指定连接', async () => {
        const calls = fakeApi();
        const user = userEvent.setup();
        render(<App />);
        await screen.findByText('测试节点');
        await user.click(screen.getByRole('button', { name: '0 / 0' }));
        expect(await screen.findByText('127.0.0.1:50000')).toBeTruthy();
        expect(screen.queryByRole('button', { name: '断开' })).toBeNull();
        await user.click(screen.getByRole('button', { name: '关闭' }));
        await user.click(screen.getByRole('button', { name: /未登录/ }));
        await user.type(screen.getByRole('textbox', { name: '用户名' }), 'admin');
        await user.type(screen.getByLabelText('密码'), 'test-password');
        await user.click(screen.getByRole('button', { name: '登录' }));
        await user.click(screen.getByRole('button', { name: '0 / 0' }));
        await screen.findByText('127.0.0.1:50000');
        await user.click(screen.getByRole('button', { name: '断开' }));
        await user.click(screen.getByRole('button', { name: '确认断开' }));
        await screen.findByText('当前没有连接');
        expect(
            calls.find((call) => call.method === 'DELETE' && call.path.includes('/connections/'))?.csrf,
        ).toBe('test-csrf');
    });

    it('匿名只读并可查看按通道聚合的历史图表', async () => {
        const calls = fakeApi(true);
        const user = userEvent.setup();
        render(<App />);
        expect(await screen.findByText('测试节点')).toBeTruthy();
        expect(screen.getByRole('button', { name: /未登录/ })).toBeTruthy();
        expect(screen.queryByRole('button', { name: '添加客户端' })).toBeNull();
        expect(screen.queryByRole('button', { name: '安全组' })).toBeNull();
        const mappingTable = within(screen.getByText('访问节点').closest('article')!).getByRole('region', {
            name: '端到端访问入口',
        });
        expect(within(mappingTable).getByRole('columnheader', { name: '入口 ID' })).toBeTruthy();
        expect(within(mappingTable).getByText('node-a-echo')).toBeTruthy();
        expect(within(mappingTable).getByText('127.0.0.1:23456')).toBeTruthy();
        expect(within(mappingTable).queryByRole('button', { name: '删除' })).toBeNull();
        expect(calls.filter((call) => call.path.startsWith('/api/v1/dashboard/snapshot?'))).toHaveLength(1);
        expect(calls.some((call) => call.path === '/api/v1/overview')).toBe(false);
        expect(calls.some((call) => call.path === '/api/v1/clients/node-b/channels')).toBe(false);
        expect(calls.some((call) => call.path === '/api/v1/clients/node-b/mappings')).toBe(false);
        expect(calls.some((call) => call.path.includes('/admin/clients/node-b/mappings'))).toBe(false);
        await user.click(screen.getByRole('button', { name: /4\.0 KiB \/ 4\.0 KiB/ }));
        expect(await screen.findByRole('img', { name: '目标和访问方每分钟流量折线图' })).toBeTruthy();
        expect(screen.getByText('最近 24 小时')).toBeTruthy();
    });

    it('安全组仅登录可管理，普通通道可选择并提交安全组', async () => {
        const calls = fakeApi();
        const user = userEvent.setup();
        render(<App />);
        await screen.findByText('测试节点');
        await user.click(screen.getByRole('button', { name: /未登录/ }));
        await user.type(screen.getByRole('textbox', { name: '用户名' }), 'admin');
        await user.type(screen.getByLabelText('密码'), 'test-password');
        await user.click(screen.getByRole('button', { name: '登录' }));
        await user.click(await screen.findByRole('button', { name: '安全组' }));
        await user.click(screen.getByRole('button', { name: '添加安全组' }));
        await user.type(screen.getByRole('textbox', { name: '安全组 ID' }), 'office');
        await user.type(screen.getByRole('textbox', { name: '名称' }), '办公室');
        await user.type(
            screen.getByRole('textbox', { name: /允许的 IP 或 CIDR/ }),
            '192.0.2.10\n198.51.100.0/24',
        );
        await user.click(screen.getByRole('button', { name: '保存安全组' }));
        await screen.findByText('办公室');
        expect(
            calls.find((call) => call.path === '/api/v1/admin/security-groups' && call.method === 'POST')
                ?.body,
        ).toMatchObject({ id: 'office', entries: ['192.0.2.10', '198.51.100.0/24'] });
        expect(
            calls.find((call) => call.path === '/api/v1/admin/security-groups' && call.method === 'POST')
                ?.csrf,
        ).toBe('test-csrf');
        await user.click(screen.getByRole('button', { name: '关闭' }));
        await user.click(screen.getByRole('button', { name: '添加通道' }));
        await user.selectOptions(screen.getByRole('combobox', { name: '安全组' }), 'office');
        await user.type(screen.getByRole('textbox', { name: '通道 ID' }), 'new');
        await user.type(screen.getByRole('textbox', { name: '显示名称' }), '新通道');
        await user.click(screen.getByRole('button', { name: '保存并下发' }));
        expect(
            calls.find((call) => call.path.endsWith('/channels') && call.method === 'POST')?.body,
        ).toHaveProperty('securityGroupId', 'office');
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
        expect(screen.getByTitle('输入大写字母会自动转为小写。').className).toBe('field-help');
        expect(screen.queryByText('输入大写字母会自动转为小写。')).toBeNull();
        expect(screen.getByRole('textbox', { name: /Agent 连接的服务端主机/ })).toHaveProperty(
            'value',
            'tunnel.example.com',
        );
        expect(screen.queryByRole('textbox', { name: /受信 CA 路径/ })).toBeNull();
        await user.type(screen.getByRole('textbox', { name: '客户端 ID' }), 'Node-B');
        expect(screen.getByRole('textbox', { name: '客户端 ID' })).toHaveProperty('value', 'node-b');
        await user.type(screen.getByRole('textbox', { name: '显示名称' }), '新节点');
        await user.click(screen.getByRole('button', { name: '保存客户端' }));
        await screen.findByText('新节点');
        expect(calls.find((c) => c.path === '/api/v1/admin/clients' && c.method === 'POST')?.csrf).toBe(
            'test-csrf',
        );
        expect(
            (
                calls.find((c) => c.path === '/api/v1/admin/clients' && c.method === 'POST')?.body as {
                    agentServerHost: string;
                }
            ).agentServerHost,
        ).toBe('tunnel.example.com');
        expect(
            calls.find((c) => c.path === '/api/v1/admin/clients' && c.method === 'POST')?.body,
        ).not.toHaveProperty('trustedCaPemPath');

        await user.click(screen.getAllByRole('button', { name: '添加通道' })[0]);
        expect(screen.getByRole('textbox', { name: '云端监听地址' })).toHaveProperty('value', '0.0.0.0');
        expect(screen.getByRole('spinbutton', { name: '云端监听端口' })).toHaveProperty('value', '19001');
        await user.type(screen.getByRole('textbox', { name: '通道 ID' }), 'new-channel');
        await user.type(screen.getByRole('textbox', { name: '显示名称' }), '新通道');
        await user.click(screen.getByRole('button', { name: '保存并下发' }));
        await screen.findByText('新通道');
        expect(calls.find((c) => c.path.endsWith('/channels') && c.method === 'POST')?.csrf).toBe(
            'test-csrf',
        );
        expect(calls.find((c) => c.path.endsWith('/channels') && c.method === 'POST')?.body).toHaveProperty(
            'listenAddress',
            '0.0.0.0',
        );
        expect(calls.find((c) => c.path.endsWith('/channels') && c.method === 'POST')?.body).toHaveProperty(
            'listenPort',
            19001,
        );
        await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());

        await user.click(screen.getAllByRole('button', { name: '添加通道' })[0]);
        expect(screen.getByRole('spinbutton', { name: '云端监听端口' })).toHaveProperty('value', '19002');
        await user.click(screen.getByRole('button', { name: '取消' }));

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

    it('授权通道和端到端访问入口由管理员配置并下发', async () => {
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
        await user.click(screen.getByRole('button', { name: '保存客户端' }));
        await screen.findByText('新节点');

        const targetCard = screen.getByText('测试节点').closest('article')!;
        await user.click(within(targetCard).getByRole('button', { name: '编辑客户端' }));
        expect(screen.getByRole('textbox', { name: /端到端证书 SHA-256 指纹/ })).toHaveProperty(
            'value',
            'A'.repeat(64),
        );
        expect(screen.getByRole('textbox', { name: /端到端证书 SHA-256 指纹/ })).toHaveProperty(
            'readOnly',
            true,
        );
        await user.click(screen.getByRole('button', { name: '取消' }));
        expect(within(targetCard).getByRole('button', { name: '添加通道' }).className).toBe(
            within(targetCard).getByRole('button', { name: '添加端到端访问入口' }).className,
        );
        await user.click(within(targetCard).getByRole('button', { name: '添加通道' }));
        await user.type(screen.getByRole('textbox', { name: '通道 ID' }), 'private');
        await user.type(screen.getByRole('textbox', { name: '显示名称' }), '私有通道');
        expect(screen.getByRole('textbox', { name: '云端监听地址' })).toHaveProperty('value', '0.0.0.0');
        expect(screen.getByRole('spinbutton', { name: '云端监听端口' })).toHaveProperty('value', '19001');
        await user.click(screen.getByRole('checkbox', { name: /仅允许授权客户端互访/ }));
        expect(screen.queryByRole('textbox', { name: '云端监听地址' })).toBeNull();
        expect(screen.queryByRole('spinbutton', { name: '云端监听端口' })).toBeNull();
        await user.click(screen.getByRole('checkbox', { name: /仅允许授权客户端互访/ }));
        expect(screen.getByRole('textbox', { name: '云端监听地址' })).toHaveProperty('value', '0.0.0.0');
        expect(screen.getByRole('spinbutton', { name: '云端监听端口' })).toHaveProperty('value', '19001');
        await user.click(screen.getByRole('checkbox', { name: /仅允许授权客户端互访/ }));
        expect(screen.queryByRole('textbox', { name: /端到端证书 SHA-256 指纹/ })).toBeNull();
        await user.click(screen.getByRole('button', { name: '保存并下发' }));
        await screen.findByText('私有通道');
        expect(calls.find((c) => c.path.endsWith('/channels') && c.method === 'POST')?.csrf).toBe(
            'test-csrf',
        );
        expect(
            calls.find((c) => c.path.endsWith('/channels') && c.method === 'POST')?.body,
        ).not.toHaveProperty('e2eCertificateSha256');

        const callerCard = screen.getByText('新节点').closest('article')!;
        await user.click(within(callerCard).getByRole('button', { name: '添加端到端访问入口' }));
        expect(
            screen.getByTitle(
                '入口 ID 为目标客户端 ID-目标通道 ID。本机端口由 Agent 自动选择并保存，冲突时自动轮换。',
            ).className,
        ).toBe('field-help');
        expect(screen.queryByText(/本机端口由 Agent 自动选择并保存/)).toBeNull();
        await user.selectOptions(screen.getByRole('combobox', { name: '目标客户端' }), 'node-a');
        await user.selectOptions(screen.getByRole('combobox', { name: '目标授权通道' }), 'private');
        expect(screen.getByRole('textbox', { name: '入口 ID（自动生成）' })).toHaveProperty(
            'value',
            'node-a-private',
        );
        expect(screen.getByRole('textbox', { name: '入口 ID（自动生成）' })).toHaveProperty('readOnly', true);
        await user.click(screen.getByRole('button', { name: '保存并下发' }));
        await screen.findByText('node-a-private');
        expect(within(callerCard).queryByRole('button', { name: '编辑' })).toBeNull();
        expect(calls.find((c) => c.path.endsWith('/mappings') && c.method === 'POST')?.csrf).toBe(
            'test-csrf',
        );
        expect(calls.find((c) => c.path.endsWith('/mappings') && c.method === 'POST')?.body).toEqual({
            targetClientId: 'node-a',
            targetChannelId: 'private',
        });
    });
});
