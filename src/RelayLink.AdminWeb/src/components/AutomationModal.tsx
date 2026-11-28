import { useEffect, useState } from 'react';
import { Modal } from './Modal';
import { time } from '../api';
import type { AutomationChange, AutomationGrant, AutomationPairing } from '../types';

const IMPLIED_READ_SCOPE: Record<string, string> = {
    'clients.write': 'clients.read',
    'channels.write': 'channels.read',
    'mappings.write': 'mappings.read',
    'security.write': 'security.read',
    'changes.write': 'changes.read',
};

function candidateScopes(requested: string[]): string[] {
    const result: string[] = [];
    const seen = new Set<string>();
    for (const scope of requested) {
        if (!seen.has(scope)) {
            seen.add(scope);
            result.push(scope);
        }
        const readScope = IMPLIED_READ_SCOPE[scope];
        if (readScope && !requested.includes(readScope) && !seen.has(readScope)) {
            seen.add(readScope);
            result.push(readScope);
        }
    }
    return result;
}

type PairingDraft = {
    scopes: string[];
    lifetimeMinutes: number;
};

export function AutomationModal({
    open,
    onClose,
    error,
    busy,
    actionBusy,
    maxSessionLifetimeMinutes,
    pairings,
    grants,
    changes,
    onDecidePairing,
    onRevokeGrant,
    onDecideChange,
}: {
    open: boolean;
    onClose: () => void;
    error?: string;
    busy: boolean;
    actionBusy: boolean;
    maxSessionLifetimeMinutes?: number | null;
    pairings: AutomationPairing[];
    grants: AutomationGrant[];
    changes: AutomationChange[];
    onDecidePairing: (
        requestId: string,
        approve: boolean,
        approvedScopes?: string[],
        lifetimeMinutes?: number,
    ) => Promise<void>;
    onRevokeGrant: (grantId: string) => Promise<void>;
    onDecideChange: (changeRequestId: string, action: 'approve' | 'reject' | 'execute') => Promise<void>;
}) {
    const maxLifetime =
        typeof maxSessionLifetimeMinutes === 'number' && maxSessionLifetimeMinutes >= 15
            ? maxSessionLifetimeMinutes
            : 120;
    const [drafts, setDrafts] = useState<Record<string, PairingDraft>>({});

    useEffect(() => {
        setDrafts((previous) => {
            const next: Record<string, PairingDraft> = {};
            for (const item of pairings) {
                const existing = previous[item.requestId];
                next[item.requestId] = existing ?? {
                    scopes: [...item.scopes],
                    lifetimeMinutes: maxLifetime,
                };
            }
            return next;
        });
    }, [pairings, maxLifetime]);

    const toggleScope = (requestId: string, requested: string[], scope: string, checked: boolean) => {
        setDrafts((previous) => {
            const current = previous[requestId] ?? { scopes: [...requested], lifetimeMinutes: maxLifetime };
            const scopes = checked
                ? current.scopes.includes(scope)
                    ? current.scopes
                    : [...current.scopes, scope]
                : current.scopes.filter((item) => item !== scope);
            return { ...previous, [requestId]: { ...current, scopes } };
        });
    };

    const setLifetime = (requestId: string, requested: string[], value: number) => {
        setDrafts((previous) => {
            const current = previous[requestId] ?? { scopes: [...requested], lifetimeMinutes: maxLifetime };
            return { ...previous, [requestId]: { ...current, lifetimeMinutes: value } };
        });
    };

    return (
        <Modal
            title="自动化授权与变更审批"
            description="核对用户码后再批准配对。用户码必须与操作员屏幕上显示的一致；批准时只能缩小 scope 与有效期。变更申请批准后才会执行一次。"
            open={open}
            onClose={onClose}
            error={error}
        >
            <div className="automation-panel">
                <section>
                    <h3>待批准的配对请求（{pairings.length}）</h3>
                    {busy && <p className="muted">加载中…</p>}
                    {!busy && pairings.length === 0 && <p className="muted">没有待批准的请求。</p>}
                    {pairings.map((item) => {
                        const draft = drafts[item.requestId] ?? {
                            scopes: item.scopes,
                            lifetimeMinutes: maxLifetime,
                        };
                        const options = candidateScopes(item.scopes);
                        const lifetimeValid =
                            Number.isInteger(draft.lifetimeMinutes) &&
                            draft.lifetimeMinutes >= 15 &&
                            draft.lifetimeMinutes <= maxLifetime;
                        const canApprove = !actionBusy && draft.scopes.length > 0 && lifetimeValid;

                        return (
                            <article key={item.requestId} className="automation-card">
                                <div>
                                    <strong className="automation-usercode">{item.userCode}</strong>
                                    <span className="muted"> ← 核对用户码后再批准</span>
                                </div>
                                <dl>
                                    <dt>主体</dt>
                                    <dd>{item.subject}</dd>
                                    <dt>平台</dt>
                                    <dd>
                                        {item.platform} / CLI {item.cliVersion}
                                    </dd>
                                    <dt>申请 scope</dt>
                                    <dd>{item.scopes.join(', ')}</dd>
                                    <dt>设备指纹</dt>
                                    <dd className="mono">{item.deviceKeyThumbprint}</dd>
                                    <dt>过期</dt>
                                    <dd>{time(item.expiresAtUtc)}</dd>
                                </dl>
                                <div className="automation-narrowing">
                                    <div className="automation-scopes" aria-label="批准 scope 缩权">
                                        {options.map((scope) => (
                                            <label className="check" key={scope}>
                                                <input
                                                    type="checkbox"
                                                    disabled={actionBusy}
                                                    checked={draft.scopes.includes(scope)}
                                                    onChange={(e) =>
                                                        toggleScope(
                                                            item.requestId,
                                                            item.scopes,
                                                            scope,
                                                            e.target.checked,
                                                        )
                                                    }
                                                />{' '}
                                                <span>{scope}</span>
                                            </label>
                                        ))}
                                    </div>
                                    <label className="field automation-lifetime">
                                        有效期（分钟，15–{maxLifetime}）
                                        <input
                                            type="number"
                                            min={15}
                                            max={maxLifetime}
                                            disabled={actionBusy}
                                            value={
                                                Number.isNaN(draft.lifetimeMinutes)
                                                    ? ''
                                                    : draft.lifetimeMinutes
                                            }
                                            onChange={(e) =>
                                                setLifetime(
                                                    item.requestId,
                                                    item.scopes,
                                                    Number(e.target.value),
                                                )
                                            }
                                        />
                                    </label>
                                </div>
                                <div className="automation-actions">
                                    <button
                                        className="primary"
                                        disabled={!canApprove}
                                        onClick={() =>
                                            void onDecidePairing(
                                                item.requestId,
                                                true,
                                                draft.scopes,
                                                draft.lifetimeMinutes,
                                            )
                                        }
                                    >
                                        批准
                                    </button>
                                    <button
                                        className="ghost"
                                        disabled={actionBusy}
                                        onClick={() => void onDecidePairing(item.requestId, false)}
                                    >
                                        拒绝
                                    </button>
                                </div>
                            </article>
                        );
                    })}
                </section>

                <section>
                    <h3>已签发的授权（{grants.length}）</h3>
                    {grants.length === 0 && <p className="muted">还没有已签发的授权。</p>}
                    {grants.map((grant) => (
                        <article key={grant.grantId} className="automation-card">
                            <dl>
                                <dt>主体</dt>
                                <dd>{grant.subject}</dd>
                                <dt>平台</dt>
                                <dd>{grant.platform}</dd>
                                <dt>scope</dt>
                                <dd>{grant.scopes.join(', ')}</dd>
                                <dt>到期</dt>
                                <dd>{time(grant.expiresAtUtc)}</dd>
                                <dt>状态</dt>
                                <dd>
                                    {grant.revokedAtUtc ? `已撤销（${time(grant.revokedAtUtc)}）` : '有效'}
                                </dd>
                            </dl>
                            {!grant.revokedAtUtc && (
                                <div className="automation-actions">
                                    <button
                                        className="ghost danger"
                                        disabled={actionBusy}
                                        onClick={() => void onRevokeGrant(grant.grantId)}
                                    >
                                        撤销授权
                                    </button>
                                </div>
                            )}
                        </article>
                    ))}
                </section>

                <section>
                    <h3>变更申请（{changes.length}）</h3>
                    {changes.length === 0 && <p className="muted">没有变更申请。</p>}
                    {changes.map((change) => (
                        <article key={change.changeRequestId} className="automation-card">
                            <dl>
                                <dt>类型</dt>
                                <dd className="mono">{change.type}</dd>
                                <dt>目标</dt>
                                <dd className="mono">{change.targetKey}</dd>
                                <dt>状态</dt>
                                <dd>{change.state}</dd>
                                <dt>申请方</dt>
                                <dd>{change.requestedBy}</dd>
                                <dt>过期</dt>
                                <dd>{time(change.expiresAtUtc)}</dd>
                                {change.reason && (
                                    <>
                                        <dt>原因</dt>
                                        <dd>{change.reason}</dd>
                                    </>
                                )}
                                <dt>载荷</dt>
                                <dd>
                                    <code>{change.payload}</code>
                                </dd>
                                {change.failureReason && (
                                    <>
                                        <dt>失败原因</dt>
                                        <dd>{change.failureReason}</dd>
                                    </>
                                )}
                            </dl>
                            <div className="automation-actions">
                                {change.state === 'pending' && (
                                    <>
                                        <button
                                            className="primary"
                                            disabled={actionBusy}
                                            onClick={() =>
                                                void onDecideChange(change.changeRequestId, 'approve')
                                            }
                                        >
                                            批准
                                        </button>
                                        <button
                                            className="ghost"
                                            disabled={actionBusy}
                                            onClick={() =>
                                                void onDecideChange(change.changeRequestId, 'reject')
                                            }
                                        >
                                            拒绝
                                        </button>
                                    </>
                                )}
                                {change.state === 'approved' && (
                                    <>
                                        <button
                                            className="primary"
                                            disabled={actionBusy}
                                            onClick={() =>
                                                void onDecideChange(change.changeRequestId, 'execute')
                                            }
                                        >
                                            执行
                                        </button>
                                        <button
                                            className="ghost"
                                            disabled={actionBusy}
                                            onClick={() =>
                                                void onDecideChange(change.changeRequestId, 'reject')
                                            }
                                        >
                                            拒绝
                                        </button>
                                    </>
                                )}
                            </div>
                        </article>
                    ))}
                </section>
            </div>
        </Modal>
    );
}
