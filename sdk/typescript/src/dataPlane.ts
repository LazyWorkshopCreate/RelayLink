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

import { connect, isIP, Socket } from "node:net";
import { lookup } from "node:dns/promises";
import { createSocket, Socket as DgramSocket } from "node:dgram";
import { request as httpRequest } from "node:http";

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
    if (transport !== "tcp" && transport !== "udp") {
      throw new RelayLinkError(InvalidUsage, "transport 必须为 tcp 或 udp。");
    }
    const clean = host.trim();
    this.host = clean.startsWith("[") && clean.endsWith("]") && clean.length > 2 ? clean.slice(1, -1) : clean;
    this.port = port;
    this.transport = transport;
  }

  endpoint(): string {
    return this.host.includes(":") ? `[${this.host}]:${this.port}` : `${this.host}:${this.port}`;
  }
}

/** TCP 数据面：一次业务流一条连接，支持单向半关闭。 */
export function connectTcp(entry: EntryDescriptor, connectTimeoutMs = 20000): Promise<Socket> {
  if (entry.transport !== "tcp") {
    return Promise.reject(new RelayLinkError(InvalidUsage, "该入口不是 TCP。"));
  }
  if (!Number.isFinite(connectTimeoutMs) || connectTimeoutMs <= 0) {
    return Promise.reject(new RelayLinkError(InvalidUsage, "connectTimeoutMs 必须大于 0。"));
  }

  return new Promise<Socket>((resolve, reject) => {
    let settled = false;
    const socket = connect({ host: entry.host, port: entry.port });
    const cleanup = () => {
      clearTimeout(timer);
      socket.off("connect", onConnect);
      socket.off("error", onError);
    };
    const timer = setTimeout(() => {
      if (settled) return;
      settled = true;
      cleanup();
      socket.destroy();
      reject(new RelayLinkError(Timeout, "连接入口超时。"));
    }, connectTimeoutMs);

    const onConnect = () => {
      if (settled) return;
      settled = true;
      cleanup();
      resolve(socket);
    };

    const onError = (error: NodeJS.ErrnoException) => {
      if (settled) return;
      settled = true;
      cleanup();
      socket.destroy();
      reject(new RelayLinkError(mapSocketError(error), `连接入口失败：${error.message}`));
    };

    socket.once("connect", onConnect);
    socket.once("error", onError);
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
export async function bindUdp(entry: EntryDescriptor): Promise<DgramSocket> {
  if (entry.transport !== "udp") {
    throw new RelayLinkError(InvalidUsage, "该入口不是 UDP。");
  }

  let family = isIP(entry.host);
  if (family === 0) {
    try {
      const resolved = await lookup(entry.host);
      family = resolved.family;
    } catch (error) {
      const msg = error instanceof Error ? error.message : String(error);
      throw new RelayLinkError(EntryNotFound, `无法解析 UDP 入口：${msg}`);
    }
  }

  const socket = createSocket(family === 6 ? "udp6" : "udp4");
  return new Promise<DgramSocket>((resolve, reject) => {
    const onError = (error: Error) => {
      socket.off("connect", onConnect);
      socket.close();
      reject(new RelayLinkError(EntryUnavailable, `绑定 UDP 入口失败：${error.message}`));
    };
    const onConnect = () => {
      socket.off("error", onError);
      resolve(socket);
    };
    socket.once("error", onError);
    socket.once("connect", onConnect);
    socket.connect(entry.port, entry.host);
  });
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
    profileId?: string;
    mappings?: Array<{
      enabled?: boolean;
      targetClientId?: string;
      targetChannelId?: string;
      localAddress?: string | null;
      transportProtocol?: string;
    }>;
  }>;
}

function parseEndpoint(address: string): { host: string; port: number } | null {
  const trimmed = address.trim();
  if (trimmed.startsWith("[")) {
    const close = trimmed.indexOf("]");
    if (close <= 1 || trimmed[close + 1] !== ":") return null;
    const host = trimmed.slice(1, close);
    const portText = trimmed.slice(close + 2);
    if (!/^\d+$/.test(portText)) return null;
    const port = Number.parseInt(portText, 10);
    return port >= 1 && port <= 65535 ? { host, port } : null;
  }
  const separator = trimmed.lastIndexOf(":");
  if (separator <= 0 || trimmed.indexOf(":") !== separator) return null;
  const host = trimmed.slice(0, separator);
  const portText = trimmed.slice(separator + 1);
  if (!/^\d+$/.test(portText)) return null;
  const port = Number.parseInt(portText, 10);
  return port >= 1 && port <= 65535 ? { host, port } : null;
}

function fetchMappingsJson(dashboardPort: number, timeoutMs = 5000): Promise<AgentSnapshot> {
  return new Promise((resolve, reject) => {
    let settled = false;
    const req = httpRequest(
      {
        host: "127.0.0.1",
        port: dashboardPort,
        path: "/api/v2/mappings",
        method: "GET"
      },
      (res) => {
        const chunks: Buffer[] = [];
        res.on("data", (chunk: Buffer) => chunks.push(chunk));
        res.on("end", () => {
          if (settled) return;
          settled = true;
          if (!res.statusCode || res.statusCode !== 200) {
            reject(new RelayLinkError(EntryUnavailable, `Agent 只读 API 返回 ${res.statusCode ?? 0}。`));
            return;
          }
          try {
            const parsed = JSON.parse(Buffer.concat(chunks).toString("utf-8")) as AgentSnapshot;
            resolve(parsed);
          } catch {
            reject(new RelayLinkError(ConfigurationDrift, "Agent 返回了无法解析的内容。"));
          }
        });
        res.on("error", (err) => {
          if (settled) return;
          settled = true;
          reject(new RelayLinkError(EntryUnavailable, `读取 Agent 响应失败：${err.message}`));
        });
      }
    );

    req.setTimeout(timeoutMs, () => {
      if (settled) return;
      settled = true;
      req.destroy(new Error("timeout"));
      reject(new RelayLinkError(Timeout, "查询本机 Agent 只读 API 超时。"));
    });

    req.on("error", (err) => {
      if (settled) return;
      settled = true;
      reject(new RelayLinkError(EntryUnavailable, `无法连接本机 Agent 只读 API：${err.message}`));
    });

    req.end();
  });
}

/**
 * 从本机 Agent 的 v2 只读 API 发现授权互访入口。
 * 只覆盖授权互访；普通代理入口必须显式配置。
 */
export async function discoverMapping(
  targetClientId: string,
  targetChannelId: string,
  dashboardPort = DEFAULT_DASHBOARD_PORT,
  profileId?: string
): Promise<EntryDescriptor> {
  if (!Number.isInteger(dashboardPort) || dashboardPort < 1 || dashboardPort > 65535) {
    throw new RelayLinkError(InvalidUsage, "dashboardPort 无效。");
  }
  if (!targetClientId || !targetChannelId) {
    throw new RelayLinkError(InvalidUsage, "targetClientId 与 targetChannelId 不能为空。");
  }

  const payload = await fetchMappingsJson(dashboardPort);
  for (const server of payload.servers ?? []) {
    if (profileId && server.profileId !== profileId) continue;
    for (const mapping of server.mappings ?? []) {
      if (mapping.enabled === false) continue;
      if (mapping.targetClientId !== targetClientId) continue;
      if (mapping.targetChannelId !== targetChannelId) continue;
      const address = mapping.localAddress;
      if (!address) continue;
      const parsed = parseEndpoint(address);
      if (!parsed) continue;
      const transport = mapping.transportProtocol === "udp" ? "udp" : "tcp";
      return new EntryDescriptor(parsed.host, parsed.port, transport);
    }
  }

  throw new RelayLinkError(EntryNotFound, "本机 Agent 没有匹配的授权互访入口。");
}
