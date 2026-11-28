import json
import socket
import threading
import unittest
from http.server import BaseHTTPRequestHandler, HTTPServer
from pathlib import Path
import sys

SDK_ROOT = Path(__file__).resolve().parents[1]
REPO_ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(SDK_ROOT))

from relaylink_sdk import (  # noqa: E402
    CONFIGURATION_DRIFT,
    ENTRY_NOT_FOUND,
    ENTRY_UNAVAILABLE,
    INVALID_USAGE,
    EntryDescriptor,
    RelayLinkError,
    TcpEntry,
    UdpEntry,
    discover_mapping,
)


def load_cases() -> dict[str, dict]:
    raw = json.loads((REPO_ROOT / "contract" / "conformance" / "data-plane.json").read_text(encoding="utf-8"))
    return {c["id"]: c for c in raw["cases"]}


class PythonSdkConformanceTests(unittest.TestCase):
    def test_conformance_cases(self) -> None:
        cases = load_cases()

        empty = cases["entry-descriptor-rejects-empty-host"]
        with self.assertRaises(RelayLinkError) as cm:
            EntryDescriptor(empty["input"]["host"], empty["input"]["port"])
        self.assertEqual(cm.exception.code, empty["expect"]["error"])

        bad_port = cases["entry-descriptor-rejects-port-range"]
        with self.assertRaises(RelayLinkError) as cm:
            EntryDescriptor(bad_port["input"]["host"], bad_port["input"]["port"])
        self.assertEqual(cm.exception.code, bad_port["expect"]["error"])

        endpoint = cases["entry-endpoint-string"]
        desc = EntryDescriptor(endpoint["input"]["host"], endpoint["input"]["port"])
        self.assertEqual(desc.endpoint(), endpoint["expect"]["endpoint"])

        refused = cases["tcp-connect-refused-maps-to-entry-unavailable"]
        with self.assertRaises(RelayLinkError) as cm:
            TcpEntry.connect(EntryDescriptor(refused["input"]["host"], refused["input"]["port"]), connect_timeout=5.0)
        self.assertEqual(cm.exception.code, refused["expect"]["error"])

        tcp_mismatch = cases["tcp-rejects-non-tcp-transport"]
        with self.assertRaises(RelayLinkError) as cm:
            TcpEntry.connect(EntryDescriptor(tcp_mismatch["input"]["host"], tcp_mismatch["input"]["port"], tcp_mismatch["input"]["transport"]))
        self.assertEqual(cm.exception.code, tcp_mismatch["expect"]["error"])

        udp_mismatch = cases["udp-rejects-non-udp-transport"]
        with self.assertRaises(RelayLinkError) as cm:
            UdpEntry.bind(EntryDescriptor(udp_mismatch["input"]["host"], udp_mismatch["input"]["port"], udp_mismatch["input"]["transport"]))
        self.assertEqual(cm.exception.code, udp_mismatch["expect"]["error"])

        oversized = cases["udp-rejects-oversized-datagram"]
        udp_entry = EntryDescriptor("127.0.0.1", 20001, "udp")
        sock = UdpEntry.bind(udp_entry)
        try:
            with self.assertRaises(RelayLinkError) as cm:
                UdpEntry.send(sock, b"x" * oversized["input"]["size"])
            self.assertEqual(cm.exception.code, oversized["expect"]["error"])
        finally:
            sock.close()

        bad_dash = cases["discovery-rejects-invalid-dashboard-port"]
        with self.assertRaises(RelayLinkError) as cm:
            discover_mapping("c", "ch", bad_dash["input"]["dashboardPort"])
        self.assertEqual(cm.exception.code, bad_dash["expect"]["error"])

        unreachable = cases["discovery-unreachable-agent"]
        with self.assertRaises(RelayLinkError) as cm:
            discover_mapping("c", "ch", unreachable["input"]["dashboardPort"])
        self.assertEqual(cm.exception.code, unreachable["expect"]["error"])

    def test_ipv6_endpoint_and_tcp_half_close_and_udp_roundtrip(self) -> None:
        self.assertEqual(EntryDescriptor("::1", 20001).endpoint(), "[::1]:20001")
        self.assertEqual(EntryDescriptor("[::1]", 20001).endpoint(), "[::1]:20001")
        with self.assertRaises(RelayLinkError) as cm:
            EntryDescriptor("   ", 8080, "tcp")
        self.assertEqual(cm.exception.code, INVALID_USAGE)
        with self.assertRaises(RelayLinkError) as cm:
            EntryDescriptor("127.0.0.1", 8080, "sctp")
        self.assertEqual(cm.exception.code, INVALID_USAGE)

        # TCP half-close
        server = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        server.bind(("127.0.0.1", 0))
        server.listen(1)
        port = server.getsockname()[1]

        def serve_tcp() -> None:
            conn, _ = server.accept()
            with conn:
                chunks = []
                while True:
                    b = conn.recv(1024)
                    if not b:
                        break
                    chunks.append(b)
                conn.sendall(b"reply:" + b"".join(chunks))

        t = threading.Thread(target=serve_tcp, daemon=True)
        t.start()
        try:
            client = TcpEntry.connect(EntryDescriptor("127.0.0.1", port))
            try:
                client.sendall(b"ping")
                TcpEntry.shutdown_write(client)
                received = b""
                while True:
                    chunk = client.recv(1024)
                    if not chunk:
                        break
                    received += chunk
                self.assertEqual(received, b"reply:ping")
            finally:
                client.close()
        finally:
            server.close()

        # UDP round-trip
        udp_srv = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        udp_srv.bind(("127.0.0.1", 0))
        udp_port = udp_srv.getsockname()[1]

        def serve_udp() -> None:
            data, addr = udp_srv.recvfrom(1024)
            udp_srv.sendto(data.upper(), addr)

        threading.Thread(target=serve_udp, daemon=True).start()
        try:
            sock = UdpEntry.bind(EntryDescriptor("127.0.0.1", udp_port, "udp"))
            try:
                sock.settimeout(2.0)
                UdpEntry.send(sock, b"hello")
                self.assertEqual(sock.recv(1024), b"HELLO")
            finally:
                sock.close()
        finally:
            udp_srv.close()

    def test_discovery_supports_udp_disabled_skip_profile_id_and_invalid_json(self) -> None:
        payload = {
            "servers": [
                {
                    "profileId": "east",
                    "mappings": [
                        {
                            "enabled": False,
                            "targetClientId": "visited",
                            "targetChannelId": "ch-udp",
                            "localAddress": "127.0.0.1:20999",
                            "transportProtocol": "udp",
                        },
                        {
                            "enabled": True,
                            "targetClientId": "visited",
                            "targetChannelId": "ch-dup",
                            "localAddress": "127.0.0.1:21001",
                            "transportProtocol": "tcp",
                        },
                    ],
                },
                {
                    "profileId": "west",
                    "mappings": [
                        {
                            "enabled": True,
                            "targetClientId": "visited",
                            "targetChannelId": "ch-udp",
                            "localAddress": "[::1]:20002",
                            "transportProtocol": "udp",
                        },
                        {
                            "enabled": True,
                            "targetClientId": "visited",
                            "targetChannelId": "ch-dup",
                            "localAddress": "127.0.0.1:22002",
                            "transportProtocol": "tcp",
                        },
                    ],
                },
            ]
        }
        state = {"raw": json.dumps(payload).encode("utf-8")}

        class Handler(BaseHTTPRequestHandler):
            def do_GET(self) -> None:
                self.send_response(200)
                self.send_header("Content-Type", "application/json")
                self.end_headers()
                self.wfile.write(state["raw"])

            def log_message(self, format: str, *args: object) -> None:
                return

        srv = HTTPServer(("127.0.0.1", 0), Handler)
        port = srv.server_address[1]
        threading.Thread(target=srv.serve_forever, daemon=True).start()
        try:
            udp_entry = discover_mapping("visited", "ch-udp", port)
            self.assertEqual(udp_entry.transport, "udp")
            self.assertEqual(udp_entry.host, "::1")
            self.assertEqual(udp_entry.port, 20002)
            self.assertEqual(udp_entry.endpoint(), "[::1]:20002")

            west_entry = discover_mapping("visited", "ch-dup", port, profile_id="west")
            self.assertEqual(west_entry.port, 22002)

            with self.assertRaises(RelayLinkError) as cm:
                discover_mapping("visited", "missing", port)
            self.assertEqual(cm.exception.code, ENTRY_NOT_FOUND)

            state["raw"] = b"{not-json"
            with self.assertRaises(RelayLinkError) as cm:
                discover_mapping("visited", "ch-udp", port)
            self.assertEqual(cm.exception.code, CONFIGURATION_DRIFT)
        finally:
            srv.shutdown()
            srv.server_close()


if __name__ == "__main__":
    unittest.main()
