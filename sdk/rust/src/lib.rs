// RelayLink SDK —— Rust 数据面。
//
// SDK 只面向「入口」，不实现中继协议，也不持有任何中继密钥。
// 不引入 async 运行时与第三方依赖，只用 std。
//
// 重要限制：普通代理入口无法从本机 Agent 的 v2 只读 API 自动发现
// （该视图只上报授权互访的 loopback 端口，不含通道的 ListenAddress/ListenPort），
// 因此普通代理入口必须显式配置。

use std::fmt;
use std::io;
use std::net::{TcpStream, UdpSocket};
use std::time::Duration;

/// 稳定错误码。各语言实现必须保持一致语义。
pub const ENTRY_UNAVAILABLE: &str = "entry_unavailable";
pub const ENTRY_NOT_FOUND: &str = "entry_not_found";
pub const TUNNEL_REJECTED: &str = "tunnel_rejected";
pub const TARGET_CONNECT_FAILED: &str = "target_connect_failed";
pub const TIMEOUT: &str = "timeout";
pub const CONFIGURATION_DRIFT: &str = "configuration_drift";
pub const INVALID_USAGE: &str = "invalid_usage";

pub const DEFAULT_DASHBOARD_PORT: u16 = 18081;
pub const MAX_DATAGRAM: usize = 65507;

/// 跨语言一致的错误类型，保留稳定 code 字符串。
#[derive(Debug)]
pub struct RelayLinkError {
    pub code: &'static str,
    pub message: String,
    pub source: Option<io::Error>,
}

impl RelayLinkError {
    pub fn new(code: &'static str, message: impl Into<String>) -> Self {
        Self { code, message: message.into(), source: None }
    }

    pub fn with_source(code: &'static str, message: impl Into<String>, source: io::Error) -> Self {
        Self { code, message: message.into(), source: Some(source) }
    }
}

impl fmt::Display for RelayLinkError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(formatter, "{}: {}", self.code, self.message)
    }
}

impl std::error::Error for RelayLinkError {}

/// 入口描述。
#[derive(Debug, Clone)]
pub struct EntryDescriptor {
    pub host: String,
    pub port: u16,
    pub transport: String,
}

impl EntryDescriptor {
    pub fn new(host: impl Into<String>, port: u16, transport: impl Into<String>) -> Result<Self, RelayLinkError> {
        let host = host.into();
        if host.is_empty() {
            return Err(RelayLinkError::new(INVALID_USAGE, "host 不能为空。"));
        }
        if port == 0 {
            return Err(RelayLinkError::new(INVALID_USAGE, "port 必须在 1–65535。"));
        }
        let transport = transport.into();
        let transport = if transport.is_empty() { "tcp".to_string() } else { transport };
        Ok(Self { host, port, transport })
    }

    pub fn endpoint(&self) -> String {
        format!("{}:{}", self.host, self.port)
    }
}

/// TCP 数据面：一次业务流一条连接，支持单向半关闭。
pub fn connect_tcp(entry: &EntryDescriptor, connect_timeout: Duration) -> Result<TcpStream, RelayLinkError> {
    if entry.transport != "tcp" {
        return Err(RelayLinkError::new(INVALID_USAGE, "该入口不是 TCP。"));
    }

    let address = format!("{}:{}", entry.host, entry.port);
    match TcpStream::connect_timeout(&resolve(&address)?, connect_timeout) {
        Ok(stream) => Ok(stream),
        Err(error) => Err(classify(&error)),
    }
}

fn resolve(address: &str) -> Result<std::net::SocketAddr, RelayLinkError> {
    use std::net::ToSocketAddrs;
    let mut addresses = address
        .to_socket_addrs()
        .map_err(|error| RelayLinkError::with_source(ENTRY_NOT_FOUND, "无法解析入口主机。", error))?;
    addresses
        .next()
        .ok_or_else(|| RelayLinkError::new(ENTRY_NOT_FOUND, "入口主机没有可用地址。"))
}

fn classify(error: &io::Error) -> RelayLinkError {
    // Windows 平台差异：连接未监听端口时 Windows 的 TCP/IP 栈对 SYN 静默丢弃，
    // connect_timeout 表现为 TimedOut（实测 raw_os_error=None），而非 Linux 的
    // ConnectionRefused。「connect 超时到未监听的回环端口」语义上就是入口不可用，
    // 与 Go/.NET/Python 实现的映射对齐；真正的慢目标超时由更长预算的场景承担。
    match error.kind() {
        io::ErrorKind::ConnectionRefused => RelayLinkError::with_source(ENTRY_UNAVAILABLE, "入口拒绝连接。", clone_error(error)),
        io::ErrorKind::TimedOut | io::ErrorKind::WouldBlock => RelayLinkError::with_source(ENTRY_UNAVAILABLE, "入口无响应（可能未监听或不可达）。", clone_error(error)),
        _ => RelayLinkError::with_source(TARGET_CONNECT_FAILED, "连接入口失败。", clone_error(error)),
    }
}

fn clone_error(error: &io::Error) -> io::Error {
    io::Error::new(error.kind(), error.to_string())
}

/// 单向半关闭：只关闭发送方向，仍可继续接收对端回包。
pub fn shutdown_write(stream: &TcpStream) -> Result<(), RelayLinkError> {
    stream
        .shutdown(std::net::Shutdown::Write)
        .map_err(|error| RelayLinkError::with_source(TUNNEL_REJECTED, "半关闭失败。", error))
}

/// UDP 数据面：单 socket 会话，保留报文边界。
pub fn bind_udp(entry: &EntryDescriptor) -> Result<UdpSocket, RelayLinkError> {
    if entry.transport != "udp" {
        return Err(RelayLinkError::new(INVALID_USAGE, "该入口不是 UDP。"));
    }
    UdpSocket::bind("0.0.0.0:0")
        .and_then(|socket| {
            socket.connect(entry.endpoint())?;
            Ok(socket)
        })
        .map_err(|error| RelayLinkError::with_source(ENTRY_UNAVAILABLE, "绑定 UDP 入口失败。", error))
}

/// 发送报文。超限时明确报错，不静默截断。
pub fn send_udp(socket: &UdpSocket, payload: &[u8]) -> Result<usize, RelayLinkError> {
    if payload.len() > MAX_DATAGRAM {
        return Err(RelayLinkError::new(INVALID_USAGE, "报文超过 UDP 上限，SDK 不静默截断。"));
    }
    socket
        .send(payload)
        .map_err(|error| RelayLinkError::with_source(TARGET_CONNECT_FAILED, "发送 UDP 报文失败。", error))
}
