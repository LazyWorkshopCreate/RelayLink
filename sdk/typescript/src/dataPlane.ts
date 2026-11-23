/**
 * RelayLink SDK —— TypeScript / Node 数据面。
 *
 * SDK 只面向「入口」，不实现中继协议，也不持有任何中继密钥。
 * 仅使用 Node 内置模块，不引入第三方依赖。
 *
 * 重要限制：普通代理入口无法从本机 Agent 的 v2 只读 API 自动发现
 * （该视图只上报授权互访的 loopback 端口，不含通道的 ListenAddress/ListenPort），
 * 因此普通代理入口必须显式配置。
 */

import { connect, Socket } from "node:net";
import { createSocket, Socket as DgramSocket } from "node:dgram";

export const EntryUnavailable = "entry_unavailable";
export const EntryNotFound = "entry_not_found";
export const TunnelRejected = "tunnel_rejected";
export const TargetConnectFailed = "target_connect_failed";
export const Timeout = "timeout";
export const ConfigurationDrift = "configuration_drift";
export const InvalidUsage = "invalid_usage";

export const DEFAULT_DASHBOARD_PORT = 18081;

/** 跨语言一致的错误类型，保留稳定 code 字符串。 */
export class RelayLinkError extends Error {
  public readonly code: string;

  constructor(code: string, message: string) {
    super(message);
    this.name = "RelayLinkError";
    this.code = code;
  }
}

export class EntryDescriptor {
  public readonly host: string;
  public readonly port: number;
  public readonly transport: "tcp" | "udp";

  constructor(host: string, port: number, transport: "tcp" | "udp" = "tcp") {
    if (!host || host.trim().length === 0) {
      throw new RelayLinkError(InvalidUsage, "host 不能为空。");
    }
    if (!Number.isInteger(port) || port < 1 || port > 65535) {
      throw new RelayLinkError(InvalidUsage, "port 必须在 1–65535。");
    }
    this.host = host;
    this.port = port;
    this.transport = transport;
  }

  endpoint(): string {
    return `${this.host}:${this.port}`;
  }
}

/** TCP 数据面：一次业务流一条连接，支持单向半关闭。 */
export function connectTcp(entry: EntryDescriptor, connectTimeoutMs = 20000): Promise<Socket> {
  if (entry.transport !== "tcp") {
    return Promise.reject(new RelayLinkError(InvalidUsage, "该入口不是 TCP。"));
  }

  return new Promise<Socket>((resolve, reject) => {
    const socket = connect({ host: entry.host, port: entry.port });
    const timer = setTimeout(() => {
      socket.destroy();
      reject(new RelayLinkError(Timeout, "连接入口超时。"));
    }, connectTimeoutMs);

    socket.once("connect", () => {
      clearTimeout(timer);
      resolve(socket);
    });

    socket.once("error", (error: NodeJS.ErrnoException) => {
      clearTimeout(timer);
      socket.destroy();
      reject(new RelayLinkError(mapSocketError(error), `连接入口失败：${error.message}`));
    });
  });
}

/** 单向半关闭：只关闭发送方向，仍可继续接收对端回包。 */
export function shutdownWrite(socket: Socket): void {
  try {
    socket.end();
  } catch (error) {
    throw new RelayLinkError(TunnelRejected, `半关闭失败：${String(error)}`);
  }
}

function mapSocketError(error: NodeJS.ErrnoException): string {
  switch (error.code) {
    case "ECONNREFUSED":
      return EntryUnavailable;
    case "ENOTFOUND":
    case "EAI_AGAIN":
      return EntryNotFound;
    case "ETIMEDOUT":
      return Timeout;
    default:
      return TargetConnectFailed;
  }
}

const MAX_DATAGRAM = 65507;

/** UDP 数据面：单 socket 会话，保留报文边界。 */
export function bindUdp(entry: EntryDescriptor): DgramSocket {
  if (entry.transport !== "udp") {
    throw new RelayLinkError(InvalidUsage, "该入口不是 UDP。");
  }

  const socket = createSocket("udp4");
  socket.connect(entry.port, entry.host);
  return socket;
}

export function sendUdp(socket: DgramSocket, payload: Buffer): Promise<void> {
  // 超限时明确报错，不静默截断。
  if (payload.length > MAX_DATAGRAM) {
    return Promise.reject(new RelayLinkError(InvalidUsage, "报文超过 UDP 上限，SDK 不静默截断。"));
  }

  return new Promise<void>((resolve, reject) => {
    socket.send(payload, (error) => (error ? reject(new RelayLinkError(TargetConnectFailed, error.message)) : resolve()));
  });
}

interface AgentSnapshot {
  servers?: Array<{
    mappings?: Array<{
      targetClientId?: string;
      targetChannelId?: string;
      localAddress?: string | null;
    }>;
  }>;
}

/**
 * 从本机 Agent 的 v2 只读 API 发现授权互访入口。
 * 只覆盖授权互访；普通代理入口必须显式配置。
 */
export async function discoverMapping(
  targetClientId: string,
  targetChannelId: string,
  dashboardPort = DEFAULT_DASHBOARD_PORT
): Promise<EntryDescriptor> {
  if (!Number.isInteger(dashboardPort) || dashboardPort < 1 || dashboardPort > 65535) {
    throw new RelayLinkError(InvalidUsage, "dashboardPort 无效。");
  }

  const url = `http://127.0.0.1:${dashboardPort}/api/v2/mappings`;
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), 5000);

  let payload: AgentSnapshot;
  try {
    const response = await fetch(url, { signal: controller.signal });
    if (!response.ok) {
      throw new RelayLinkError(EntryUnavailable, `Agent 只读 API 返回 ${response.status}。`);
    }
    payload = (await response.json()) as AgentSnapshot;
  } catch (error) {
    if (error instanceof RelayLinkError) throw error;
    const message = error instanceof Error ? error.message : String(error);
    throw new RelayLinkError(EntryUnavailable, `无法连接本机 Agent 只读 API：${message}`);
  } finally {
    clearTimeout(timer);
  }

  for (const server of payload.servers ?? []) {
    for (const mapping of server.mappings ?? []) {
      if (mapping.targetClientId !== targetClientId) continue;
      if (mapping.targetChannelId !== targetChannelId) continue;
      const address = mapping.localAddress;
      if (!address) continue;
      const separator = address.lastIndexOf(":");
      if (separator <= 0) continue;
      const host = address.slice(0, separator);
      const port = Number.parseInt(address.slice(separator + 1), 10);
      if (!Number.isInteger(port)) continue;
      return new EntryDescriptor(host, port);
    }
  }

  throw new RelayLinkError(EntryNotFound, "本机 Agent 没有匹配的授权互访入口。");
}
