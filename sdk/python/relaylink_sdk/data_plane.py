"""RelayLink SDK —— Python 数据面。

SDK 只面向「入口」，不实现中继协议，也不持有任何中继密钥。
仅使用标准库，不引入第三方依赖。

重要限制：普通代理入口无法从本机 Agent 的 v2 只读 API 自动发现
（该视图只上报授权互访的 loopback 端口，不含通道的 ListenAddress/ListenPort），
因此普通代理入口必须显式配置。
"""

from __future__ import annotations

import json
import socket
import urllib.error
import urllib.request
from typing import Optional

ENTRY_UNAVAILABLE = "entry_unavailable"
ENTRY_NOT_FOUND = "entry_not_found"
TUNNEL_REJECTED = "tunnel_rejected"
TARGET_CONNECT_FAILED = "target_connect_failed"
TIMEOUT = "timeout"
CONFIGURATION_DRIFT = "configuration_drift"
INVALID_USAGE = "invalid_usage"

DEFAULT_DASHBOARD_PORT = 18081


class RelayLinkError(Exception):
    """跨语言一致的错误类型，保留稳定 code 字符串。"""

    def __init__(self, code: str, message: str) -> None:
        super().__init__(message)
        self.code = code
        self.message = message


class EntryDescriptor:
    """入口描述。"""

    def __init__(self, host: str, port: int, transport: str = "tcp") -> None:
        if not host or not host.strip():
            raise RelayLinkError(INVALID_USAGE, "host 不能为空。")
        if isinstance(port, bool) or not 1 <= port <= 65535:
            raise RelayLinkError(INVALID_USAGE, "port 必须在 1–65535。")
        if transport not in ("tcp", "udp"):
            raise RelayLinkError(INVALID_USAGE, "transport 必须为 tcp 或 udp。")
        clean = host.strip()
        if clean.startswith("[") and clean.endswith("]") and len(clean) > 2:
            clean = clean[1:-1]
        self.host = clean
        self.port = port
        self.transport = transport

    def endpoint(self) -> str:
        if ":" in self.host:
            return f"[{self.host}]:{self.port}"
        return f"{self.host}:{self.port}"

    def __repr__(self) -> str:
        return f"EntryDescriptor({self.endpoint()}, {self.transport})"


class TcpEntry:
    """TCP 数据面：一次业务流一条连接，支持单向半关闭。"""

    @staticmethod
    def connect(entry: EntryDescriptor, connect_timeout: float = 20.0) -> socket.socket:
        if entry.transport != "tcp":
            raise RelayLinkError(INVALID_USAGE, "该入口不是 TCP。")
        if connect_timeout <= 0:
            raise RelayLinkError(INVALID_USAGE, "connect_timeout 必须大于 0。")

        try:
            return socket.create_connection((entry.host, entry.port), timeout=connect_timeout)
        except (socket.timeout, TimeoutError) as exc:
            raise RelayLinkError(TIMEOUT, "连接入口超时。") from exc
        except ConnectionRefusedError as exc:
            raise RelayLinkError(ENTRY_UNAVAILABLE, "入口拒绝连接。") from exc
        except socket.gaierror as exc:
            raise RelayLinkError(ENTRY_NOT_FOUND, "无法解析入口主机。") from exc
        except OSError as exc:
            raise RelayLinkError(TARGET_CONNECT_FAILED, f"连接入口失败：{exc}") from exc

    @staticmethod
    def shutdown_write(sock: socket.socket) -> None:
        """单向半关闭：只关闭发送方向，仍可继续接收对端回包。"""
        try:
            sock.shutdown(socket.SHUT_WR)
        except OSError as exc:
            raise RelayLinkError(TUNNEL_REJECTED, f"半关闭失败：{exc}") from exc


class UdpEntry:
    """UDP 数据面：单 socket 会话，保留报文边界。"""

    MAX_DATAGRAM = 65507

    @staticmethod
    def bind(entry: EntryDescriptor) -> socket.socket:
        if entry.transport != "udp":
            raise RelayLinkError(INVALID_USAGE, "该入口不是 UDP。")
        try:
            infos = socket.getaddrinfo(entry.host, entry.port, type=socket.SOCK_DGRAM)
        except socket.gaierror as exc:
            raise RelayLinkError(ENTRY_NOT_FOUND, f"无法解析 UDP 入口：{exc}") from exc
        if not infos:
            raise RelayLinkError(ENTRY_NOT_FOUND, "无法解析 UDP 入口。")
        last_error: Optional[OSError] = None
        for family, socktype, proto, _, sockaddr in infos:
            sock = socket.socket(family, socktype, proto)
            try:
                sock.connect(sockaddr)
                return sock
            except OSError as exc:
                sock.close()
                last_error = exc
        raise RelayLinkError(ENTRY_UNAVAILABLE, f"绑定 UDP 入口失败：{last_error}")

    @staticmethod
    def send(sock: socket.socket, payload: bytes) -> None:
        # 超限时明确报错，不静默截断。
        if len(payload) > UdpEntry.MAX_DATAGRAM:
            raise RelayLinkError(INVALID_USAGE, "报文超过 UDP 上限，SDK 不静默截断。")
        try:
            sock.send(payload)
        except OSError as exc:
            raise RelayLinkError(TARGET_CONNECT_FAILED, f"发送 UDP 报文失败：{exc}") from exc


class _NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):  # type: ignore[override]
        return None


def _parse_endpoint(address: str) -> Optional[tuple[str, int]]:
    trimmed = address.strip()
    if trimmed.startswith("["):
        close = trimmed.find("]")
        if close <= 1 or close + 2 >= len(trimmed) or trimmed[close + 1] != ":":
            return None
        host = trimmed[1:close]
        port_text = trimmed[close + 2:]
    else:
        if trimmed.count(":") != 1:
            return None
        host, _, port_text = trimmed.partition(":")
    if not host or not port_text.isdigit():
        return None
    port = int(port_text)
    if not 1 <= port <= 65535:
        return None
    return host, port


def discover_mapping(
    target_client_id: str,
    target_channel_id: str,
    dashboard_port: int = DEFAULT_DASHBOARD_PORT,
    profile_id: Optional[str] = None,
) -> EntryDescriptor:
    """从本机 Agent 的 v2 只读 API 发现授权互访入口。

    只覆盖授权互访；普通代理入口必须显式配置。
    """
    if isinstance(dashboard_port, bool) or not 1 <= dashboard_port <= 65535:
        raise RelayLinkError(INVALID_USAGE, "dashboardPort 无效。")
    if not target_client_id or not target_channel_id:
        raise RelayLinkError(INVALID_USAGE, "targetClientId 与 targetChannelId 不能为空。")

    url = f"http://127.0.0.1:{dashboard_port}/api/v2/mappings"
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), _NoRedirect())
    try:
        with opener.open(url, timeout=5) as response:
            payload = json.loads(response.read().decode("utf-8"))
    except TimeoutError as exc:
        raise RelayLinkError(TIMEOUT, "查询本机 Agent 只读 API 超时。") from exc
    except urllib.error.HTTPError as exc:
        raise RelayLinkError(ENTRY_UNAVAILABLE, f"Agent 只读 API 返回 {exc.code}。") from exc
    except urllib.error.URLError as exc:
        if isinstance(exc.reason, TimeoutError):
            raise RelayLinkError(TIMEOUT, "查询本机 Agent 只读 API 超时。") from exc
        raise RelayLinkError(ENTRY_UNAVAILABLE, "无法连接本机 Agent 只读 API。") from exc
    except (json.JSONDecodeError, UnicodeDecodeError) as exc:
        raise RelayLinkError(CONFIGURATION_DRIFT, "Agent 返回了无法解析的内容。") from exc

    for server in payload.get("servers", []):
        if profile_id is not None and server.get("profileId") != profile_id:
            continue
        for mapping in server.get("mappings", []):
            if mapping.get("enabled") is False:
                continue
            if mapping.get("targetClientId") != target_client_id:
                continue
            if mapping.get("targetChannelId") != target_channel_id:
                continue
            address: Optional[str] = mapping.get("localAddress")
            if not address:
                continue
            parsed = _parse_endpoint(address)
            if not parsed:
                continue
            host, port = parsed
            transport = mapping.get("transportProtocol") or "tcp"
            if transport not in ("tcp", "udp"):
                transport = "tcp"
            return EntryDescriptor(host, port, transport)

    raise RelayLinkError(ENTRY_NOT_FOUND, "本机 Agent 没有匹配的授权互访入口。")
