package relaylink

import (
	"encoding/json"
	"os"
	"path/filepath"
	"testing"
)

// 一致性用例直接读取 contract/conformance/data-plane.json，
// 保证各语言实现面对同一批场景给出同一批错误码。

type conformanceCase struct {
	ID     string `json:"id"`
	Area   string `json:"area"`
	Input  struct {
		Host      string `json:"host"`
		Port      int    `json:"port"`
		Transport string `json:"transport"`
		Size      int    `json:"size"`
	} `json:"input"`
	Expect struct {
		Error    string `json:"error"`
		Endpoint string `json:"endpoint"`
	} `json:"expect"`
}

func loadCases(t *testing.T) []conformanceCase {
	t.Helper()
	// 仓库根 = go.mod 上溯三级（sdk/go -> sdk -> 仓库根）
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
	if !errorsAs(err, &rlErr) || rlErr.Code != "invalid_usage" {
		t.Fatalf("case %s: expected invalid_usage, got %v", empty.ID, err)
	}

	badPort := findCase(t, cases, "entry-descriptor-rejects-port-range")
	_, err = NewEntryDescriptor(badPort.Input.Host, badPort.Input.Port, "tcp")
	if !errorsAs(err, &rlErr) || rlErr.Code != "invalid_usage" {
		t.Fatalf("case %s: expected invalid_usage, got %v", badPort.ID, err)
	}

	endpoint := findCase(t, cases, "entry-endpoint-string")
	entry, err := NewEntryDescriptor(endpoint.Input.Host, endpoint.Input.Port, "tcp")
	if err != nil {
		t.Fatalf("case %s: unexpected error %v", endpoint.ID, err)
	}
	if entry.Endpoint() != endpoint.Expect.Endpoint {
		t.Fatalf("case %s: expected endpoint %q, got %q", endpoint.ID, endpoint.Expect.Endpoint, entry.Endpoint())
	}
}

func TestConformance_TcpConnectRefused(t *testing.T) {
	cases := loadCases(t)
	refused := findCase(t, cases, "tcp-connect-refused-maps-to-entry-unavailable")

	entry, err := NewEntryDescriptor(refused.Input.Host, refused.Input.Port, "tcp")
	if err != nil {
		t.Fatalf("unexpected descriptor error: %v", err)
	}
	_, dialErr := ConnectTCP(entry, 2000000000) // 200ms
	if dialErr == nil {
		t.Fatalf("case %s: expected %q, dial to port %d unexpectedly succeeded", refused.ID, refused.Expect.Error, refused.Input.Port)
	}
	var rlErr *RelayLinkError
	if !errorsAs(dialErr, &rlErr) || rlErr.Code != refused.Expect.Error {
		t.Fatalf("case %s: expected %q, got %v", refused.ID, refused.Expect.Error, dialErr)
	}
}

func TestConformance_TransportMismatch(t *testing.T) {
	cases := loadCases(t)
	casesList := cases

	tcpOnUdp := findCase(t, casesList, "tcp-rejects-non-tcp-transport")
	entry, err := NewEntryDescriptor(tcpOnUdp.Input.Host, tcpOnUdp.Input.Port, tcpOnUdp.Input.Transport)
	if err != nil {
		t.Fatalf("descriptor error: %v", err)
	}
	if _, dialErr := ConnectTCP(entry, 0); dialErr == nil {
		t.Fatalf("case %s: expected error for non-tcp transport", tcpOnUdp.ID)
	}

	udpOnTcp := findCase(t, casesList, "udp-rejects-non-udp-transport")
	udpEntry, err := NewEntryDescriptor(udpOnTcp.Input.Host, udpOnTcp.Input.Port, udpOnTcp.Input.Transport)
	if err != nil {
		t.Fatalf("descriptor error: %v", err)
	}
	if _, bindErr := BindUDP(udpEntry); bindErr == nil {
		t.Fatalf("case %s: expected error for non-udp transport", udpOnTcp.ID)
	}
}

// errorsAs 是 errors.As 的最小封装，避免每个用例重复类型断言样板。
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