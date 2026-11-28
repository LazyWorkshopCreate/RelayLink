// RelayLink SDK —— Rust 数据面。
//
// SDK 只面向「入口」，不实现中继协议，也不持有任何中继密钥。
// 不引入 async 运行时与第三方依赖，只用 std。
//
// 重要限制：普通代理入口无法从本机 Agent 的 v2 只读 API 自动发现
// （该视图只上报授权互访的 loopback 端口，不含通道的 ListenAddress/ListenPort），
// 因此普通代理入口必须显式配置。

use std::collections::BTreeMap;
use std::fmt;
use std::io::{self, Read, Write};
use std::net::{SocketAddr, TcpStream, ToSocketAddrs, UdpSocket};
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
        let raw_host = host.into();
        let trimmed = raw_host.trim();
        if trimmed.is_empty() {
            return Err(RelayLinkError::new(INVALID_USAGE, "host 不能为空。"));
        }
        let clean_host = if trimmed.starts_with('[') && trimmed.ends_with(']') && trimmed.len() > 2 {
            trimmed[1..trimmed.len() - 1].to_string()
        } else {
            trimmed.to_string()
        };
        if port == 0 {
            return Err(RelayLinkError::new(INVALID_USAGE, "port 必须在 1–65535。"));
        }
        let transport = transport.into();
        let transport = if transport.is_empty() { "tcp".to_string() } else { transport };
        if transport != "tcp" && transport != "udp" {
            return Err(RelayLinkError::new(INVALID_USAGE, "transport 必须为 tcp 或 udp。"));
        }
        Ok(Self { host: clean_host, port, transport })
    }

    pub fn endpoint(&self) -> String {
        if self.host.contains(':') {
            format!("[{}]:{}", self.host, self.port)
        } else {
            format!("{}:{}", self.host, self.port)
        }
    }
}

/// TCP 数据面：一次业务流一条连接，支持单向半关闭。
pub fn connect_tcp(entry: &EntryDescriptor, connect_timeout: Duration) -> Result<TcpStream, RelayLinkError> {
    if entry.transport != "tcp" {
        return Err(RelayLinkError::new(INVALID_USAGE, "该入口不是 TCP。"));
    }
    if connect_timeout.is_zero() {
        return Err(RelayLinkError::new(INVALID_USAGE, "connect_timeout 必须大于 0。"));
    }

    let addr = resolve(&entry.endpoint())?;
    match TcpStream::connect_timeout(&addr, connect_timeout) {
        Ok(stream) => Ok(stream),
        Err(error) => Err(classify(&error)),
    }
}

fn resolve(address: &str) -> Result<SocketAddr, RelayLinkError> {
    let mut addresses = address
        .to_socket_addrs()
        .map_err(|error| RelayLinkError::with_source(ENTRY_NOT_FOUND, "无法解析入口主机。", error))?;
    addresses
        .next()
        .ok_or_else(|| RelayLinkError::new(ENTRY_NOT_FOUND, "入口主机没有可用地址。"))
}

fn classify(error: &io::Error) -> RelayLinkError {
    if error.kind() == io::ErrorKind::ConnectionRefused || error.raw_os_error() == Some(10061) {
        return RelayLinkError::with_source(ENTRY_UNAVAILABLE, "入口拒绝连接。", clone_error(error));
    }
    match error.kind() {
        io::ErrorKind::TimedOut | io::ErrorKind::WouldBlock => RelayLinkError::with_source(TIMEOUT, "连接入口超时。", clone_error(error)),
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
    let addr = resolve(&entry.endpoint())?;
    let bind_addr = if addr.is_ipv6() { "[::]:0" } else { "0.0.0.0:0" };
    UdpSocket::bind(bind_addr)
        .and_then(|socket| {
            socket.connect(addr)?;
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

/// 从本机 Agent 的 v2 只读 API 发现授权互访入口。
pub fn discover_mapping(
    target_client_id: &str,
    target_channel_id: &str,
    dashboard_port: u16,
) -> Result<EntryDescriptor, RelayLinkError> {
    discover_mapping_for_profile(target_client_id, target_channel_id, dashboard_port, None)
}

/// 按可选 profile_id 从本机 Agent 的 v2 只读 API 发现授权互访入口。
pub fn discover_mapping_for_profile(
    target_client_id: &str,
    target_channel_id: &str,
    dashboard_port: u16,
    profile_id: Option<&str>,
) -> Result<EntryDescriptor, RelayLinkError> {
    if dashboard_port == 0 {
        return Err(RelayLinkError::new(INVALID_USAGE, "dashboardPort 无效。"));
    }
    if target_client_id.is_empty() || target_channel_id.is_empty() {
        return Err(RelayLinkError::new(INVALID_USAGE, "targetClientId 与 targetChannelId 不能为空。"));
    }

    let addr = SocketAddr::from(([127, 0, 0, 1], dashboard_port));
    let timeout = Duration::from_secs(5);
    let mut stream = TcpStream::connect_timeout(&addr, timeout)
        .map_err(|err| classify_http_io(&err))?;
    let _ = stream.set_read_timeout(Some(timeout));
    let _ = stream.set_write_timeout(Some(timeout));

    let req = format!(
        "GET /api/v2/mappings HTTP/1.1\r\nHost: 127.0.0.1:{dashboard_port}\r\nConnection: close\r\nAccept-Encoding: identity\r\n\r\n"
    );
    stream
        .write_all(req.as_bytes())
        .map_err(|err| classify_http_io(&err))?;

    let mut raw = Vec::new();
    stream
        .read_to_end(&mut raw)
        .map_err(|err| classify_http_io(&err))?;

    let body = extract_http_body(&raw)?;
    let root = JsonParser::parse(&body)
        .map_err(|_| RelayLinkError::new(CONFIGURATION_DRIFT, "Agent 返回了无法解析的内容。"))?;

    let servers = root
        .as_object()
        .and_then(|obj| obj.get("servers"))
        .and_then(|val| val.as_array())
        .ok_or_else(|| RelayLinkError::new(ENTRY_NOT_FOUND, "Agent 未返回 servers。"))?;

    for server in servers {
        let Some(server_obj) = server.as_object() else { continue };
        if let Some(wanted_profile) = profile_id {
            if !wanted_profile.is_empty()
                && server_obj.get("profileId").and_then(|v| v.as_str()) != Some(wanted_profile)
            {
                continue;
            }
        }
        let Some(mappings) = server_obj.get("mappings").and_then(|v| v.as_array()) else {
            continue;
        };
        for mapping in mappings {
            let Some(m_obj) = mapping.as_object() else { continue };
            if m_obj.get("enabled").and_then(|v| v.as_bool()) == Some(false) {
                continue;
            }
            if m_obj.get("targetClientId").and_then(|v| v.as_str()) != Some(target_client_id) {
                continue;
            }
            if m_obj.get("targetChannelId").and_then(|v| v.as_str()) != Some(target_channel_id) {
                continue;
            }
            let Some(address) = m_obj.get("localAddress").and_then(|v| v.as_str()) else {
                continue;
            };
            let Some((host, port)) = parse_endpoint(address) else {
                continue;
            };
            let transport = m_obj
                .get("transportProtocol")
                .and_then(|v| v.as_str())
                .filter(|s| !s.is_empty())
                .unwrap_or("tcp");
            return EntryDescriptor::new(host, port, transport);
        }
    }

    Err(RelayLinkError::new(ENTRY_NOT_FOUND, "本机 Agent 没有匹配的授权互访入口。"))
}

fn classify_http_io(err: &io::Error) -> RelayLinkError {
    match err.kind() {
        io::ErrorKind::TimedOut | io::ErrorKind::WouldBlock => {
            RelayLinkError::with_source(TIMEOUT, "查询本机 Agent 只读 API 超时。", clone_error(err))
        }
        _ => RelayLinkError::with_source(ENTRY_UNAVAILABLE, "无法连接本机 Agent 只读 API。", clone_error(err)),
    }
}

fn parse_endpoint(address: &str) -> Option<(String, u16)> {
    let trimmed = address.trim();
    if let Some(rest) = trimmed.strip_prefix('[') {
        let close = rest.find(']')?;
        let host = &rest[..close];
        let after = &rest[close + 1..];
        let port_str = after.strip_prefix(':')?;
        let port: u16 = port_str.parse().ok()?;
        if port == 0 || host.is_empty() {
            return None;
        }
        return Some((host.to_string(), port));
    }
    let sep = trimmed.rfind(':')?;
    if trimmed[..sep].contains(':') || sep == 0 {
        return None;
    }
    let port: u16 = trimmed[sep + 1..].parse().ok()?;
    if port == 0 {
        return None;
    }
    Some((trimmed[..sep].to_string(), port))
}

fn extract_http_body(raw: &[u8]) -> Result<String, RelayLinkError> {
    let sep = raw
        .windows(4)
        .position(|w| w == b"\r\n\r\n")
        .ok_or_else(|| RelayLinkError::new(CONFIGURATION_DRIFT, "Agent HTTP 响应格式无效。"))?;
    let head = std::str::from_utf8(&raw[..sep])
        .map_err(|_| RelayLinkError::new(CONFIGURATION_DRIFT, "Agent HTTP 响应头不是 UTF-8。"))?;
    let mut lines = head.split("\r\n");
    let status_line = lines.next().unwrap_or_default();
    let status_code = status_line
        .split_whitespace()
        .nth(1)
        .and_then(|s| s.parse::<u16>().ok())
        .unwrap_or(0);
    if status_code != 200 {
        return Err(RelayLinkError::new(
            ENTRY_UNAVAILABLE,
            format!("Agent 只读 API 返回 {status_code}。"),
        ));
    }

    let mut is_chunked = false;
    let mut content_length: Option<usize> = None;
    for line in lines {
        if let Some((k, v)) = line.split_once(':') {
            let key = k.trim();
            let val = v.trim();
            if key.eq_ignore_ascii_case("Transfer-Encoding") && val.to_ascii_lowercase().contains("chunked") {
                is_chunked = true;
            } else if key.eq_ignore_ascii_case("Content-Length") {
                content_length = val.parse().ok();
            }
        }
    }

    let rest = &raw[sep + 4..];
    let bytes = if is_chunked {
        decode_chunked(rest)?
    } else if let Some(len) = content_length {
        rest.get(..len)
            .ok_or_else(|| RelayLinkError::new(CONFIGURATION_DRIFT, "Agent 响应体被截断。"))?
            .to_vec()
    } else {
        rest.to_vec()
    };

    String::from_utf8(bytes).map_err(|_| RelayLinkError::new(CONFIGURATION_DRIFT, "Agent 响应体不是 UTF-8。"))
}

fn decode_chunked(mut data: &[u8]) -> Result<Vec<u8>, RelayLinkError> {
    let mut out = Vec::new();
    loop {
        let pos = data
            .windows(2)
            .position(|w| w == b"\r\n")
            .ok_or_else(|| RelayLinkError::new(CONFIGURATION_DRIFT, "Agent 分块响应无效。"))?;
        let size_line = std::str::from_utf8(&data[..pos])
            .map_err(|_| RelayLinkError::new(CONFIGURATION_DRIFT, "Agent 分块响应头无效。"))?;
        let size_hex = size_line.split(';').next().unwrap_or_default().trim();
        let size = usize::from_str_radix(size_hex, 16)
            .map_err(|_| RelayLinkError::new(CONFIGURATION_DRIFT, "Agent 分块长度无效。"))?;
        data = &data[pos + 2..];
        if size == 0 {
            break;
        }
        if data.len() < size + 2 {
            return Err(RelayLinkError::new(CONFIGURATION_DRIFT, "Agent 分块数据不完整。"));
        }
        out.extend_from_slice(&data[..size]);
        data = &data[size + 2..];
    }
    Ok(out)
}

#[derive(Debug)]
enum JsonVal {
    Null,
    Bool(bool),
    Num,
    Str(String),
    Arr(Vec<JsonVal>),
    Obj(BTreeMap<String, JsonVal>),
}

impl JsonVal {
    fn as_object(&self) -> Option<&BTreeMap<String, JsonVal>> {
        if let JsonVal::Obj(m) = self { Some(m) } else { None }
    }
    fn as_array(&self) -> Option<&Vec<JsonVal>> {
        if let JsonVal::Arr(a) = self { Some(a) } else { None }
    }
    fn as_str(&self) -> Option<&str> {
        if let JsonVal::Str(s) = self { Some(s.as_str()) } else { None }
    }
    fn as_bool(&self) -> Option<bool> {
        if let JsonVal::Bool(b) = self { Some(*b) } else { None }
    }
}

struct JsonParser<'a> {
    bytes: &'a [u8],
    pos: usize,
}

impl<'a> JsonParser<'a> {
    fn parse(input: &'a str) -> Result<JsonVal, ()> {
        let mut p = Self { bytes: input.as_bytes(), pos: 0 };
        let val = p.read_value()?;
        p.skip_ws();
        if p.pos != p.bytes.len() {
            return Err(());
        }
        Ok(val)
    }

    fn skip_ws(&mut self) {
        while self.pos < self.bytes.len() && matches!(self.bytes[self.pos], b' ' | b'\t' | b'\r' | b'\n') {
            self.pos += 1;
        }
    }

    fn read_value(&mut self) -> Result<JsonVal, ()> {
        self.skip_ws();
        if self.pos >= self.bytes.len() {
            return Err(());
        }
        match self.bytes[self.pos] {
            b'{' => self.read_obj(),
            b'[' => self.read_arr(),
            b'"' => Ok(JsonVal::Str(self.read_str()?)),
            b't' => self.read_lit(b"true", JsonVal::Bool(true)),
            b'f' => self.read_lit(b"false", JsonVal::Bool(false)),
            b'n' => self.read_lit(b"null", JsonVal::Null),
            b'-' | b'0'..=b'9' => self.read_num(),
            _ => Err(()),
        }
    }

    fn read_obj(&mut self) -> Result<JsonVal, ()> {
        self.pos += 1;
        self.skip_ws();
        let mut map = BTreeMap::new();
        if self.bytes.get(self.pos) == Some(&b'}') {
            self.pos += 1;
            return Ok(JsonVal::Obj(map));
        }
        loop {
            self.skip_ws();
            if self.bytes.get(self.pos) != Some(&b'"') {
                return Err(());
            }
            let key = self.read_str()?;
            self.skip_ws();
            if self.bytes.get(self.pos) != Some(&b':') {
                return Err(());
            }
            self.pos += 1;
            let val = self.read_value()?;
            map.insert(key, val);
            self.skip_ws();
            match self.bytes.get(self.pos) {
                Some(&b'}') => {
                    self.pos += 1;
                    return Ok(JsonVal::Obj(map));
                }
                Some(&b',') => self.pos += 1,
                _ => return Err(()),
            }
        }
    }

    fn read_arr(&mut self) -> Result<JsonVal, ()> {
        self.pos += 1;
        self.skip_ws();
        let mut arr = Vec::new();
        if self.bytes.get(self.pos) == Some(&b']') {
            self.pos += 1;
            return Ok(JsonVal::Arr(arr));
        }
        loop {
            arr.push(self.read_value()?);
            self.skip_ws();
            match self.bytes.get(self.pos) {
                Some(&b']') => {
                    self.pos += 1;
                    return Ok(JsonVal::Arr(arr));
                }
                Some(&b',') => self.pos += 1,
                _ => return Err(()),
            }
        }
    }

    fn read_str(&mut self) -> Result<String, ()> {
        self.pos += 1;
        let mut raw = Vec::new();
        while self.pos < self.bytes.len() {
            let b = self.bytes[self.pos];
            self.pos += 1;
            if b == b'"' {
                return String::from_utf8(raw).map_err(|_| ());
            }
            if b < 0x20 {
                return Err(());
            }
            if b == b'\\' {
                if self.pos >= self.bytes.len() {
                    return Err(());
                }
                let esc = self.bytes[self.pos];
                self.pos += 1;
                match esc {
                    b'"' | b'\\' | b'/' => raw.push(esc),
                    b'b' => raw.push(0x08),
                    b'f' => raw.push(0x0c),
                    b'n' => raw.push(b'\n'),
                    b'r' => raw.push(b'\r'),
                    b't' => raw.push(b'\t'),
                    b'u' => {
                        if self.pos + 4 > self.bytes.len() {
                            return Err(());
                        }
                        let hex = std::str::from_utf8(&self.bytes[self.pos..self.pos + 4]).map_err(|_| ())?;
                        let code = u16::from_str_radix(hex, 16).map_err(|_| ())?;
                        self.pos += 4;
                        let ch = char::from_u32(u32::from(code)).ok_or(())?;
                        let mut buf = [0u8; 4];
                        raw.extend_from_slice(ch.encode_utf8(&mut buf).as_bytes());
                    }
                    _ => return Err(()),
                }
            } else {
                raw.push(b);
            }
        }
        Err(())
    }

    fn read_lit(&mut self, lit: &[u8], val: JsonVal) -> Result<JsonVal, ()> {
        if self.bytes.get(self.pos..self.pos + lit.len()) == Some(lit) {
            self.pos += lit.len();
            Ok(val)
        } else {
            Err(())
        }
    }

    fn read_num(&mut self) -> Result<JsonVal, ()> {
        if self.bytes.get(self.pos) == Some(&b'-') {
            self.pos += 1;
        }
        if !matches!(self.bytes.get(self.pos), Some(b'0'..=b'9')) {
            return Err(());
        }
        while matches!(self.bytes.get(self.pos), Some(b'0'..=b'9')) {
            self.pos += 1;
        }
        if self.bytes.get(self.pos) == Some(&b'.') {
            self.pos += 1;
            if !matches!(self.bytes.get(self.pos), Some(b'0'..=b'9')) {
                return Err(());
            }
            while matches!(self.bytes.get(self.pos), Some(b'0'..=b'9')) {
                self.pos += 1;
            }
        }
        if matches!(self.bytes.get(self.pos), Some(b'e' | b'E')) {
            self.pos += 1;
            if matches!(self.bytes.get(self.pos), Some(b'+' | b'-')) {
                self.pos += 1;
            }
            if !matches!(self.bytes.get(self.pos), Some(b'0'..=b'9')) {
                return Err(());
            }
            while matches!(self.bytes.get(self.pos), Some(b'0'..=b'9')) {
                self.pos += 1;
            }
        }
        Ok(JsonVal::Num)
    }
}
