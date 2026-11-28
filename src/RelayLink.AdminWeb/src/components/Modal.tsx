import { useState, type ReactNode } from 'react';
import * as Dialog from '@radix-ui/react-dialog';
import { Check, Copy, X } from 'lucide-react';
import { fmt, time } from '../api';
import type { HistorySample } from '../types';

export function Modal({
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

export function NumberField({
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

export function TrafficChart({ samples }: { samples: HistorySample[] }) {
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

export function CopyEndpointButton({ value }: { value: string }) {
    const [copied, setCopied] = useState(false);
    return (
        <button
            type="button"
            className={`copy-button${copied ? ' copied' : ''}`}
            aria-label={copied ? `已复制 ${value}` : `复制 ${value}`}
            title={copied ? '已复制' : '复制地址和端口'}
            onClick={() => {
                const write = navigator.clipboard?.writeText(value);
                if (!write) return;
                void write.then(
                    () => {
                        setCopied(true);
                        window.setTimeout(() => setCopied(false), 1500);
                    },
                    () => undefined,
                );
            }}
        >
            {copied ? <Check size={13} /> : <Copy size={13} />}
        </button>
    );
}
