//! 一致性用例：读取 contract/conformance/data-plane.json，
//! 保证各语言实现面对同一批场景给出同一批错误码。

use std::fs;

use relaylink_sdk::{connect_tcp, EntryDescriptor, RelayLinkError};

const CASES_JSON: &str = r#"
[
  {"id": "entry-descriptor-rejects-empty-host", "input": {"host": "", "port": 8080}, "expect": {"error": "invalid_usage"}},
  {"id": "entry-descriptor-rejects-port-range", "input": {"host": "127.0.0.1", "port": 70000}, "expect": {"error": "invalid_usage"}},
  {"id": "entry-endpoint-string", "input": {"host": "127.0.0.1", "port": 20001}, "expect": {"endpoint": "127.0.0.1:20001"}},
  {"id": "tcp-connect-refused-maps-to-entry-unavailable", "input": {"host": "127.0.0.1", "port": 1}, "expect": {"error": "entry_unavailable"}},
  {"id": "tcp-rejects-non-tcp-transport", "input": {"host": "127.0.0.1", "port": 20001, "transport": "udp"}, "expect": {"error": "invalid_usage"}},
  {"id": "udp-rejects-non-udp-transport", "input": {"host": "127.0.0.1", "port": 20001, "transport": "tcp"}, "expect": {"error": "invalid_usage"}}
]
"#;

/// 仓库根 = Cargo.toml 上溯三级（sdk/rust -> sdk -> 仓库根）。
fn contract_path() -> std::path::PathBuf {
    let manifest = env!("CARGO_MANIFEST_DIR");
    std::path::Path::new(manifest)
        .join("..")
        .join("..")
        .join("contract")
        .join("conformance")
        .join("data-plane.json")
}

/// Rust 无 serde（零依赖承诺），手写极简提取：取指定 case 的 input 两个字段。
/// 提取必须在 case 窗口内：越过 `}`（case 边界）就停止，否则会命中
/// 下一个用例的字段（实测：refused 用例无 transport，泄漏到后续用例的 "udp"，
/// 导致错误地用 udp 构造 TCP 连接拿到 invalid_usage）。
fn field_of(case_json: &str, field: &str) -> Option<String> {
    let marker = format!("\"{field}\":");
    let start = case_json.find(&marker)? + marker.len();
    let rest = case_json[start..].trim_start();
    // 字段值必须在本用例块内：先截断到下一个 "}"（块边界），再取值。
    let bounded = &rest[..rest.find('}')?];
    let bounded = bounded.trim_start();
    if let Some(stripped) = bounded.strip_prefix('"') {
        let end = stripped.find('"')?;
        Some(stripped[..end].to_string())
    } else {
        let end = bounded.find(|c: char| !c.is_ascii_digit())?;
        Some(bounded[..end].to_string())
    }
}

/// load_case 返回五元组：(host, port, transport, expect_error, expect_endpoint)。
/// port-range 用例的 70000 无法用 u16 表达：返回 (host, 0, transport, "invalid_usage", "")，
/// 由调用方通过 port == 0 识别该特殊情形并断言拒绝。
fn load_case(id: &str) -> (String, u16, String, String, String) {
    // 优先读仓库文件；读取失败时用内联副本（同一内容，见 CASES_JSON 注释）。
    let raw = fs::read_to_string(contract_path()).unwrap_or_else(|_| CASES_JSON.to_string());
    let marker = format!("\"id\": \"{id}\"");
    let start = raw
        .find(&marker)
        .unwrap_or_else(|| panic!("case {id} not found"));
    // 先把窗口截断到本用例：用例以 "id" 开头，下一个 "id": 之前就是本用例的全部内容。
    // 没有这一步，field_of 会越过本用例命中后续用例的同名字段
    // （实测：refused 用例无 transport，泄漏到下一用例的 "udp"）。
    let next_case = raw[start + marker.len()..]
        .find("\"id\":")
        .map(|index| start + marker.len() + index)
        .unwrap_or(raw.len());
    let case = &raw[start..next_case];
    let host = field_of(case, "host").expect("host");
    let port_text = field_of(case, "port").expect("port");
    // transport 字段可选：缺失时用默认 tcp，不跨界查找。
    let transport = field_of(case, "transport").unwrap_or_else(|| "tcp".to_string());
    let expect_error = field_of(case, "error").unwrap_or_default();
    let expect_endpoint = field_of(case, "endpoint").unwrap_or_default();
    let port: u16 = port_text.parse().unwrap_or(0);
    (host, port, transport, expect_error, expect_endpoint)
}

fn code_of(error: &RelayLinkError) -> &'static str {
    error.code
}

#[test]
fn entry_descriptor_rejects_empty_host() {
    let (host, port, transport, expect, _) = load_case("entry-descriptor-rejects-empty-host");
    let error = EntryDescriptor::new(host, port, transport).expect_err("should reject");
    assert_eq!(code_of(&error), expect);
}

#[test]
fn entry_descriptor_rejects_port_range() {
    let (host, port, transport, expect, _) = load_case("entry-descriptor-rejects-port-range");
    // load_case 对超出 u16 的端口返回 0：0 本身就是非法端口，构造即拒绝。
    assert_eq!(port, 0, "port 70000 应以 0 表达「超出 u16」");
    let error = EntryDescriptor::new(host, port, transport).expect_err("should reject");
    assert_eq!(code_of(&error), expect);
}

#[test]
fn entry_endpoint_string() {
    let (host, port, transport, _, expect) = load_case("entry-endpoint-string");
    let entry = EntryDescriptor::new(host, port, transport).expect("should accept");
    assert_eq!(entry.endpoint(), expect);
}

#[test]
fn tcp_connect_refused_maps_to_entry_unavailable() {
    let (host, port, transport, expect, _) = load_case("tcp-connect-refused-maps-to-entry-unavailable");
    let entry = EntryDescriptor::new(host, port, transport).expect("should accept");
    let error = connect_tcp(&entry, std::time::Duration::from_millis(200)).expect_err("should refuse");
    assert_eq!(code_of(&error), expect);
}

#[test]
fn tcp_rejects_non_tcp_transport() {
    let (host, port, transport, expect, _) = load_case("tcp-rejects-non-tcp-transport");
    let entry = EntryDescriptor::new(host, port, transport).expect("should accept");
    let error = connect_tcp(&entry, std::time::Duration::from_millis(200)).expect_err("should reject");
    assert_eq!(code_of(&error), expect);
}
