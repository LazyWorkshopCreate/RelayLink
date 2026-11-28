package relaylink

import (
	"bytes"
	"encoding/json"
	"io"
	"net"
	"net/http"
	"net/http/httptest"
	"net/url"
	"os"
	"path/filepath"
	"strconv"
	"testing"
	"time"
)

// 一致性用例直接读取 contract/conformance/data-plane.json，
// 保证各语言实现面对同一批场景给出同一批错误码。

type conformanceCase struct {
	ID    string `json:"id"`
	Area  string `json:"area"`
	Input struct {
		Host          string `json:"host"`
		Port          int    `json:"port"`
		Transport     string `json:"transport"`
		Size          int    `json:"size"`
		DashboardPort *int   `json:"dashboardPort"`
	} `json:"input"`
	Expect struct {
		Error    string `json:"error"`
		Endpoint string `json:"endpoint"`
	} `json:"expect"`
}

func loadCases(t *testing.T) []conformanceCase {
	t.Helper()
	root, err := filepath.Abs(filepath.Join("..", "..", ".."))
	if err != nil {
		t.Fatalf("resolve repo root: %v", err)
	}
	path := filepath.Join(root, "contract", "conformance", "data-plane.json")
	raw, err := os.ReadFile(path)
	if err != nil {
		t.Fatalf("read conformance file: %v", err)
	}
	var doc struct {
		Cases []conformanceCase `json:"cases"`
	}
	if err := json.Unmarshal(raw, &doc); err != nil {
		t.Fatalf("parse conformance file: %v", err)
	}
	if len(doc.Cases) == 0 {
		t.Fatal("conformance file has no cases")
	}
	return doc.Cases
}

func findCase(t *testing.T, cases []conformanceCase, id string) conformanceCase {
	t.Helper()
	for _, c := range cases {
		if c.ID == id {
			return c
		}
	}
	t.Fatalf("case %s not found", id)
	return conformanceCase{}
}

func TestConformance_EntryDescriptor(t *testing.T) {
	cases := loadCases(t)

	empty := findCase(t, cases, "entry-descriptor-rejects-empty-host")
	_, err := NewEntryDescriptor(empty.Input.Host, empty.Input.Port, "tcp")
	var rlErr *RelayLinkError
	if !errorsAs(err, &rlErr) || rlErr.Code != empty.Expect.Error {
		t.Fatalf("case %s: expected %s, got %v", empty.ID, empty.Expect.Error, err)
	}

	badPort := findCase(t, cases, "entry-descriptor-rejects-port-range")
	_, err = NewEntryDescriptor(badPort.Input.Host, badPort.Input.Port, "tcp")
	if !errorsAs(err, &rlErr) || rlErr.Code != badPort.Expect.Error {
		t.Fatalf("case %s: expected %s, got %v", badPort.ID, badPort.Expect.Error, err)
	}

	endpoint := findCase(t, cases, "entry-endpoint-string")
	entry, err := NewEntryDescriptor(endpoint.Input.Host, endpoint.Input.Port, "tcp")
	if err != nil {
		t.Fatalf("case %s: unexpected error %v", endpoint.ID, err)
	}
	if entry.Endpoint() != endpoint.Expect.Endpoint {
		t.Fatalf("case %s: expected endpoint %q, got %q", endpoint.ID, endpoint.Expect.Endpoint, entry.Endpoint())
	}

	ipv6, err := NewEntryDescriptor("::1", 20001, "tcp")
	if err != nil || ipv6.Endpoint() != "[::1]:20001" {
		t.Fatalf("expected IPv6 endpoint [::1]:20001, got %v %v", ipv6, err)
	}
	if _, err := NewEntryDescriptor("127.0.0.1", 8080, "sctp"); !errorsAs(err, &rlErr) || rlErr.Code != InvalidUsage {
		t.Fatalf("expected invalid_usage for unknown transport, got %v", err)
	}
}

func TestConformance_TcpConnectRefused(t *testing.T) {
	cases := loadCases(t)
	refused := findCase(t, cases, "tcp-connect-refused-maps-to-entry-unavailable")

	entry, err := NewEntryDescriptor(refused.Input.Host, refused.Input.Port, "tcp")
	if err != nil {
		t.Fatalf("unexpected descriptor error: %v", err)
	}
	_, dialErr := ConnectTCP(entry, 5*time.Second)
	if dialErr == nil {
		t.Fatalf("case %s: expected %q, dial unexpectedly succeeded", refused.ID, refused.Expect.Error)
	}
	var rlErr *RelayLinkError
	if !errorsAs(dialErr, &rlErr) || rlErr.Code != refused.Expect.Error {
		t.Fatalf("case %s: expected %q, got %v", refused.ID, refused.Expect.Error, dialErr)
	}
}

func TestConformance_TransportMismatchAndUdpOversized(t *testing.T) {
	cases := loadCases(t)

	tcpOnUdp := findCase(t, cases, "tcp-rejects-non-tcp-transport")
	entry, err := NewEntryDescriptor(tcpOnUdp.Input.Host, tcpOnUdp.Input.Port, tcpOnUdp.Input.Transport)
	if err != nil {
		t.Fatalf("descriptor error: %v", err)
	}
	var rlErr *RelayLinkError
	if _, dialErr := ConnectTCP(entry, time.Second); !errorsAs(dialErr, &rlErr) || rlErr.Code != tcpOnUdp.Expect.Error {
		t.Fatalf("case %s: expected %s, got %v", tcpOnUdp.ID, tcpOnUdp.Expect.Error, dialErr)
	}

	udpOnTcp := findCase(t, cases, "udp-rejects-non-udp-transport")
	udpEntry, err := NewEntryDescriptor(udpOnTcp.Input.Host, udpOnTcp.Input.Port, udpOnTcp.Input.Transport)
	if err != nil {
		t.Fatalf("descriptor error: %v", err)
	}
	if _, bindErr := BindUDP(udpEntry); !errorsAs(bindErr, &rlErr) || rlErr.Code != udpOnTcp.Expect.Error {
		t.Fatalf("case %s: expected %s, got %v", udpOnTcp.ID, udpOnTcp.Expect.Error, bindErr)
	}

	oversized := findCase(t, cases, "udp-rejects-oversized-datagram")
	udpTarget, err := NewEntryDescriptor("127.0.0.1", 20001, "udp")
	if err != nil {
		t.Fatalf("descriptor error: %v", err)
	}
	udpConn, err := BindUDP(udpTarget)
	if err != nil {
		t.Fatalf("bind udp error: %v", err)
	}
	defer udpConn.Close()
	if sendErr := SendUDP(udpConn, make([]byte, oversized.Input.Size)); !errorsAs(sendErr, &rlErr) || rlErr.Code != oversized.Expect.Error {
		t.Fatalf("case %s: expected %s, got %v", oversized.ID, oversized.Expect.Error, sendErr)
	}
}

func TestConformance_DiscoveryCases(t *testing.T) {
	cases := loadCases(t)
	var rlErr *RelayLinkError

	badDash := findCase(t, cases, "discovery-rejects-invalid-dashboard-port")
	if _, err := DiscoverMapping("c", "ch", *badDash.Input.DashboardPort); !errorsAs(err, &rlErr) || rlErr.Code != badDash.Expect.Error {
		t.Fatalf("case %s: expected %s, got %v", badDash.ID, badDash.Expect.Error, err)
	}

	unreachable := findCase(t, cases, "discovery-unreachable-agent")
	if _, err := DiscoverMapping("c", "ch", *unreachable.Input.DashboardPort); !errorsAs(err, &rlErr) || rlErr.Code != unreachable.Expect.Error {
		t.Fatalf("case %s: expected %s, got %v", unreachable.ID, unreachable.Expect.Error, err)
	}
}

func TestTcpHalfCloseAndUdpRoundtrip(t *testing.T) {
	ln, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatalf("listen tcp: %v", err)
	}
	defer ln.Close()

	go func() {
		conn, err := ln.Accept()
		if err != nil {
			return
		}
		defer conn.Close()
		received, _ := io.ReadAll(conn)
		_, _ = conn.Write(append([]byte("ok:"), received...))
	}()

	addr := ln.Addr().(*net.TCPAddr)
	entry, _ := NewEntryDescriptor("127.0.0.1", addr.Port, "tcp")
	conn, err := ConnectTCP(entry, 5*time.Second)
	if err != nil {
		t.Fatalf("connect tcp: %v", err)
	}
	defer conn.Close()
	if _, err := conn.Write([]byte("ping")); err != nil {
		t.Fatalf("write: %v", err)
	}
	if err := ShutdownWrite(conn); err != nil {
		t.Fatalf("shutdown write: %v", err)
	}
	reply, err := io.ReadAll(conn)
	if err != nil || string(reply) != "ok:ping" {
		t.Fatalf("expected ok:ping, got %q err=%v", string(reply), err)
	}

	pc, err := net.ListenPacket("udp", "127.0.0.1:0")
	if err != nil {
		t.Fatalf("listen udp: %v", err)
	}
	defer pc.Close()
	go func() {
		buf := make([]byte, 1024)
		n, remote, err := pc.ReadFrom(buf)
		if err == nil {
			_, _ = pc.WriteTo(bytes.ToUpper(buf[:n]), remote)
		}
	}()
	udpAddr := pc.LocalAddr().(*net.UDPAddr)
	uEntry, _ := NewEntryDescriptor("127.0.0.1", udpAddr.Port, "udp")
	uConn, err := BindUDP(uEntry)
	if err != nil {
		t.Fatalf("bind udp: %v", err)
	}
	defer uConn.Close()
	_ = SendUDP(uConn, []byte("hello"))
	_ = uConn.SetReadDeadline(time.Now().Add(2 * time.Second))
	buf := make([]byte, 64)
	n, err := uConn.Read(buf)
	if err != nil || string(buf[:n]) != "HELLO" {
		t.Fatalf("expected HELLO, got %q err=%v", string(buf[:n]), err)
	}
}

func TestDiscovery_SupportsUdpDisabledSkipProfileAndDrift(t *testing.T) {
	payload := `{
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
	}`
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		_, _ = io.WriteString(w, payload)
	}))
	defer srv.Close()

	u, _ := url.Parse(srv.URL)
	port, _ := strconv.Atoi(u.Port())

	desc, err := DiscoverMapping("v", "u", port)
	if err != nil || desc.Transport != "udp" || desc.Host != "::1" || desc.Port != 20002 || desc.Endpoint() != "[::1]:20002" {
		t.Fatalf("unexpected udp discovery result: %#v err=%v", desc, err)
	}

	west, err := DiscoverMappingForProfile("v", "dup", port, "west")
	if err != nil || west.Port != 22002 {
		t.Fatalf("unexpected profile discovery result: %#v err=%v", west, err)
	}

	var rlErr *RelayLinkError
	if _, err := DiscoverMapping("v", "missing", port); !errorsAs(err, &rlErr) || rlErr.Code != EntryNotFound {
		t.Fatalf("expected entry_not_found, got %v", err)
	}

	payload = "{not-json"
	if _, err := DiscoverMapping("v", "u", port); !errorsAs(err, &rlErr) || rlErr.Code != ConfigurationDrift {
		t.Fatalf("expected configuration_drift, got %v", err)
	}
}

func errorsAs(err error, target **RelayLinkError) bool {
	for err != nil {
		if e, ok := err.(*RelayLinkError); ok {
			*target = e
			return true
		}
		u, ok := err.(interface{ Unwrap() error })
		if !ok {
			return false
		}
		err = u.Unwrap()
	}
	return false
}
