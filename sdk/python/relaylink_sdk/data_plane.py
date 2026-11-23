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
        if not host:
            raise RelayLinkError(INVALID_USAGE, "host 不能为空。")
        if not 1 <= port <= 65535:
            raise RelayLinkError(INVALID_USAGE, "port 必须在 1–65535。")
        self.host = host
        self.port = port
        self.transport = transport

    def endpoint(self) -> str:
        return f"{self.host}:{self.port}"

    def __repr__(self) -> str:
        return f"EntryDescriptor({self.endpoint()}, {self.transport})"


class TcpEntry:
    """TCP 数据面：一次业务流一条连接，支持单向半关闭。"""

    @staticmethod
    def connect(entry: EntryDescriptor, connect_timeout: float = 20.0) -> socket.socket:
        if entry.transport != "tcp":
            raise RelayLinkError(INVALID_USAGE, "该入口不是 TCP。")

        sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        sock.settimeout(connect_timeout)
        try:
            sock.connect((entry.host, entry.port))
            return sock
        except socket.timeout as exc:
            sock.close()
            raise RelayLinkError(TIMEOUT, "连接入口超时。") from exc
        except ConnectionRefusedError as exc:
            sock.close()
            raise RelayLinkError(ENTRY_UNAVAILABLE, "入口拒绝连接。") from exc
        except socket.gaierror as exc:
            sock.close()
            raise RelayLinkError(ENTRY_NOT_FOUND, "无法解析入口主机。") from exc
        except OSError as exc:
            sock.close()
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
        sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        try:
            sock.connect((entry.host, entry.port))
            return sock
        except OSError as exc:
            sock.close()
            raise RelayLinkError(ENTRY_UNAVAILABLE, f"绑定 UDP 入口失败：{exc}") from exc

    @staticmethod
    def send(sock: socket.socket, payload: bytes) -> None:
        # 超限时明确报错，不静默截断。
        if len(payload) > UdpEntry.MAX_DATAGRAM:
            raise RelayLinkError(INVALID_USAGE, "报文超过 UDP 上限，SDK 不静默截断。")
        sock.send(payload)


def discover_mapping(target_client_id: str, target_channel_id: str,
                     dashboard_port: int = DEFAULT_DASHBOARD_PORT) -> EntryDescriptor:
    """从本机 Agent 的 v2 只读 API 发现授权互访入口。

    只覆盖授权互访；普通代理入口必须显式配置。
    """
    if not 1 <= dashboard_port <= 65535:
        raise RelayLinkError(INVALID_USAGE, "dashboardPort 无效。")

    url = f"http://127.0.0.1:{dashboard_port}/api/v2/mappings"
    try:
        with urllib.request.urlopen(url, timeout=5) as response:
            payload = json.loads(response.read().decode("utf-8"))
    except (urllib.error.URLError, TimeoutError) as exc:
        raise RelayLinkError(ENTRY_UNAVAILABLE, "无法连接本机 Agent 只读 API。") from exc
    except json.JSONDecodeError as exc:
        raise RelayLinkError(CONFIGURATION_DRIFT, "Agent 返回了无法解析的内容。") from exc

    for server in payload.get("servers", []):
        for mapping in server.get("mappings", []):
            if mapping.get("targetClientId") != target_client_id:
                continue
            if mapping.get("targetChannelId") != target_channel_id:
                continue
            address: Optional[str] = mapping.get("localAddress")
            if not address:
                continue
            host, _, port_text = address.rpartition(":")
            if not host or not port_text.isdigit():
                continue
            return EntryDescriptor(host, int(port_text))

    raise RelayLinkError(ENTRY_NOT_FOUND, "本机 Agent 没有匹配的授权互访入口。")
