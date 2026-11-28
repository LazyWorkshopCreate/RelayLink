//! 一致性用例：读取 contract/conformance/data-plane.json，
//! 保证各语言实现面对同一批场景给出同一批错误码。

use std::fs;
use std::io::{Read, Write};
use std::net::{TcpListener, UdpSocket};
use std::thread;
use std::time::Duration;

use relaylink_sdk::{
    bind_udp, connect_tcp, discover_mapping, discover_mapping_for_profile, send_udp, shutdown_write,
    EntryDescriptor, RelayLinkError, CONFIGURATION_DRIFT, ENTRY_NOT_FOUND, INVALID_USAGE, TIMEOUT,
};

fn contract_path() -> std::path::PathBuf {
    let manifest = env!("CARGO_MANIFEST_DIR");
    std::path::Path::new(manifest)
        .join("..")
        .join("..")
        .join("contract")
        .join("conformance")
        .join("data-plane.json")
}

fn field_of(case_json: &str, field: &str) -> Option<String> {
    let marker = format!("\"{field}\":");
    let start = case_json.find(&marker)? + marker.len();
    let rest = case_json[start..].trim_start();
    let bounded = &rest[..rest.find('}')?];
    let bounded = bounded.trim_start();
    if let Some(stripped) = bounded.strip_prefix('"') {
        let end = stripped.find('"')?;
        Some(stripped[..end].to_string())
    } else {
        let end = bounded.find(|c: char| !c.is_ascii_digit()).unwrap_or(bounded.len());
        Some(bounded[..end].to_string())
    }
}

fn load_case(id: &str) -> (String, u16, usize, String, String, String) {
    let raw = fs::read_to_string(contract_path()).expect("contract/conformance/data-plane.json");
    let marker = format!("\"id\": \"{id}\"");
    let start = raw.find(&marker).unwrap_or_else(|| panic!("case {id} not found"));
    let next_case = raw[start + marker.len()..]
        .find("\"id\":")
        .map(|index| start + marker.len() + index)
        .unwrap_or(raw.len());
    let case = &raw[start..next_case];
    let host = field_of(case, "host").unwrap_or_default();
    let port_text = field_of(case, "port")
        .or_else(|| field_of(case, "dashboardPort"))
        .unwrap_or_default();
    let size = field_of(case, "size").and_then(|s| s.parse::<usize>().ok()).unwrap_or(0);
    let transport = field_of(case, "transport").unwrap_or_else(|| "tcp".to_string());
    let expect_error = field_of(case, "error").unwrap_or_default();
    let expect_endpoint = field_of(case, "endpoint").unwrap_or_default();
    let port: u16 = port_text.parse().unwrap_or(0);
    (host, port, size, transport, expect_error, expect_endpoint)
}

fn code_of(error: &RelayLinkError) -> &'static str {
    error.code
}

#[test]
fn entry_descriptor_cases_and_ipv6() {
    let (host, port, _, transport, expect, _) = load_case("entry-descriptor-rejects-empty-host");
    assert_eq!(code_of(&EntryDescriptor::new(host, port, transport).expect_err("reject")), expect);

    let (host, port, _, transport, expect, _) = load_case("entry-descriptor-rejects-port-range");
    assert_eq!(port, 0);
    assert_eq!(code_of(&EntryDescriptor::new(host, port, transport).expect_err("reject")), expect);

    let (host, port, _, transport, _, expect) = load_case("entry-endpoint-string");
    let entry = EntryDescriptor::new(host, port, transport).expect("accept");
    assert_eq!(entry.endpoint(), expect);

    let v6 = EntryDescriptor::new("::1", 20001, "tcp").expect("accept v6");
    assert_eq!(v6.endpoint(), "[::1]:20001");
    assert_eq!(EntryDescriptor::new("[::1]", 20001, "tcp").expect("accept v6 bracket").endpoint(), "[::1]:20001");
    assert_eq!(code_of(&EntryDescriptor::new("127.0.0.1", 8080, "sctp").expect_err("reject")), INVALID_USAGE);
}

#[test]
fn tcp_connect_refused_and_mismatch_and_half_close() {
    let (host, port, _, transport, expect, _) = load_case("tcp-connect-refused-maps-to-entry-unavailable");
    let entry = EntryDescriptor::new(host, port, transport).expect("accept");
    let error = connect_tcp(&entry, Duration::from_secs(5)).expect_err("should refuse");
    assert_eq!(code_of(&error), expect);

    let (host, port, _, transport, expect, _) = load_case("tcp-rejects-non-tcp-transport");
    let entry = EntryDescriptor::new(host, port, transport).expect("accept");
    assert_eq!(code_of(&connect_tcp(&entry, Duration::from_secs(1)).expect_err("reject")), expect);

    let listener = TcpListener::bind("127.0.0.1:0").expect("bind");
    let server_port = listener.local_addr().unwrap().port();
    thread::spawn(move || {
        if let Ok((mut conn, _)) = listener.accept() {
            let mut req = Vec::new();
            let _ = conn.read_to_end(&mut req);
            let mut reply = b"ok:".to_vec();
            reply.extend_from_slice(&req);
            let _ = conn.write_all(&reply);
        }
    });
    let client_entry = EntryDescriptor::new("127.0.0.1", server_port, "tcp").unwrap();
    let mut stream = connect_tcp(&client_entry, Duration::from_secs(5)).expect("connect");
    stream.write_all(b"ping").unwrap();
    shutdown_write(&stream).unwrap();
    let mut reply = String::new();
    stream.read_to_string(&mut reply).unwrap();
    assert_eq!(reply, "ok:ping");
}

#[test]
fn udp_mismatch_oversized_and_roundtrip() {
    let (host, port, _, transport, expect, _) = load_case("udp-rejects-non-udp-transport");
    let entry = EntryDescriptor::new(host, port, transport).expect("accept");
    assert_eq!(code_of(&bind_udp(&entry).expect_err("reject")), expect);

    let (_, _, size, _, expect, _) = load_case("udp-rejects-oversized-datagram");
    let udp_entry = EntryDescriptor::new("127.0.0.1", 20001, "udp").unwrap();
    let sock = bind_udp(&udp_entry).unwrap();
    let oversized = vec![1u8; size];
    assert_eq!(code_of(&send_udp(&sock, &oversized).expect_err("reject")), expect);

    let srv = UdpSocket::bind("127.0.0.1:0").unwrap();
    let srv_port = srv.local_addr().unwrap().port();
    thread::spawn(move || {
        let mut buf = [0u8; 64];
        if let Ok((n, peer)) = srv.recv_from(&mut buf) {
            let _ = srv.send_to(buf[..n].to_ascii_uppercase().as_slice(), peer);
        }
    });
    let client = bind_udp(&EntryDescriptor::new("127.0.0.1", srv_port, "udp").unwrap()).unwrap();
    client.set_read_timeout(Some(Duration::from_secs(2))).unwrap();
    send_udp(&client, b"ping").unwrap();
    let mut buf = [0u8; 64];
    let n = client.recv(&mut buf).unwrap();
    assert_eq!(&buf[..n], b"PING");
}

#[test]
fn discovery_conformance_timeout_and_profile_disambiguation() {
    let (_, port, _, _, expect, _) = load_case("discovery-rejects-invalid-dashboard-port");
    assert_eq!(code_of(&discover_mapping("c", "ch", port).expect_err("reject")), expect);

    let (_, port, _, _, expect, _) = load_case("discovery-unreachable-agent");
    assert_eq!(code_of(&discover_mapping("c", "ch", port).expect_err("reject")), expect);

    // Timeout case: verify classify maps TimedOut -> TIMEOUT (not ENTRY_UNAVAILABLE).
    if cfg!(windows) {
        let closed_probe = TcpListener::bind("127.0.0.1:0").unwrap();
        let closed_port = closed_probe.local_addr().unwrap().port();
        drop(closed_probe);
        let timeout_err = connect_tcp(
            &EntryDescriptor::new("127.0.0.1", closed_port, "tcp").unwrap(),
            Duration::from_millis(20),
        )
        .expect_err("20ms on closed loopback port times out on Windows before 2s RST");
        assert_eq!(code_of(&timeout_err), TIMEOUT);
    }

    let listener = TcpListener::bind("127.0.0.1:0").unwrap();
    let port = listener.local_addr().unwrap().port();
    thread::spawn(move || {
        let json = r#"{
            "servers": [
                {
                    "profileId": "east",
                    "mappings": [
                        { "enabled": false, "targetClientId": "v", "targetChannelId": "u", "localAddress": "127.0.0.1:20999", "transportProtocol": "udp" },
                        { "enabled": true, "targetClientId": "v", "targetChannelId": "dup", "localAddress": "127.0.0.1:21001", "transportProtocol": "tcp" }
                    ]
                },
                {
                    "profileId": "west",
                    "mappings": [
                        { "enabled": true, "targetClientId": "v", "targetChannelId": "u", "localAddress": "[::1]:20002", "transportProtocol": "udp" },
                        { "enabled": true, "targetClientId": "v", "targetChannelId": "dup", "localAddress": "127.0.0.1:22002", "transportProtocol": "tcp" }
                    ]
                }
            ]
        }"#;
        for step in 0..4 {
            let Ok((mut conn, _)) = listener.accept() else { break };
            let mut req = [0u8; 512];
            let _ = conn.read(&mut req);
            let body = if step < 3 { json } else { "{corrupt" };
            let resp = format!("HTTP/1.1 200 OK\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{}", body.len(), body);
            let _ = conn.write_all(resp.as_bytes());
        }
    });

    let udp = discover_mapping("v", "u", port).expect("discover udp");
    assert_eq!(udp.transport, "udp");
    assert_eq!(udp.host, "::1");
    assert_eq!(udp.port, 20002);
    assert_eq!(udp.endpoint(), "[::1]:20002");

    let west = discover_mapping_for_profile("v", "dup", port, Some("west")).expect("discover west");
    assert_eq!(west.port, 22002);

    assert_eq!(code_of(&discover_mapping("v", "missing", port).expect_err("not found")), ENTRY_NOT_FOUND);
    assert_eq!(code_of(&discover_mapping("v", "u", port).expect_err("bad json")), CONFIGURATION_DRIFT);
}
