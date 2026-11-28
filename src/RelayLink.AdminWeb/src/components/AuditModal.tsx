import { Modal } from './Modal';
import { fmt, time } from '../api';
import { auditLabels, type AuditPage } from '../types';

export function AuditModal({
    open,
    onClose,
    error,
    busy,
    page,
    pageNumber,
    hours,
    eventType,
    clientId,
    onHoursChange,
    onEventTypeChange,
    onClientIdChange,
    onLoad,
}: {
    open: boolean;
    onClose: () => void;
    error?: string;
    busy: boolean;
    page: AuditPage;
    pageNumber: number;
    hours: number;
    eventType: string;
    clientId: string;
    onHoursChange: (hours: number) => void;
    onEventTypeChange: (eventType: string) => void;
    onClientIdChange: (clientId: string) => void;
    onLoad: (pageNumber?: number) => Promise<void>;
}) {
    return (
        <Modal
            title="安全与连接审计日志"
            description="仅记录管理员会话、Agent 认证与连接生命周期，不含密钥和业务载荷；保留期随历史库清理。"
            open={open}
            onClose={onClose}
            error={error}
            wide
        >
            <form
                className="audit-filters"
                onSubmit={(event) => {
                    event.preventDefault();
                    void onLoad(1);
                }}
            >
                <label>
                    时间范围
                    <select value={hours} onChange={(event) => onHoursChange(Number(event.target.value))}>
                        <option value={24}>最近 24 小时</option>
                        <option value={168}>最近 7 天</option>
                        <option value={720}>最近 30 天</option>
                        <option value={2160}>最近 90 天</option>
                    </select>
                </label>
                <label>
                    事件
                    <select value={eventType} onChange={(event) => onEventTypeChange(event.target.value)}>
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
                        value={clientId}
                        maxLength={128}
                        onChange={(event) => onClientIdChange(event.target.value)}
                        placeholder="全部客户端"
                    />
                </label>
                <button className="primary" disabled={busy}>
                    查询
                </button>
            </form>
            <div className="audit-summary">
                共 {page.total} 条 · 第 {pageNumber} 页{' '}
                <button className="text-button" disabled={busy} onClick={() => void onLoad(pageNumber)}>
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
                        {page.events.map((item) => (
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
                        {!page.events.length && (
                            <tr>
                                <td className="empty-row" colSpan={7}>
                                    {busy ? '加载中…' : '所选范围没有审计事件'}
                                </td>
                            </tr>
                        )}
                    </tbody>
                </table>
            </div>
            <div className="audit-pages">
                <button
                    className="ghost"
                    disabled={busy || pageNumber <= 1}
                    onClick={() => void onLoad(pageNumber - 1)}
                >
                    上一页
                </button>
                <button
                    className="ghost"
                    disabled={busy || pageNumber * 50 >= page.total}
                    onClick={() => void onLoad(pageNumber + 1)}
                >
                    下一页
                </button>
            </div>
        </Modal>
    );
}
