// RelayLink SDK —— Go 数据面。
//
// SDK 只面向「入口」，不实现中继协议，也不持有任何中继密钥。
// 仅使用标准库，不引入第三方依赖。
//
// 重要限制：普通代理入口无法从本机 Agent 的 v2 只读 API 自动发现
// （该视图只上报授权互访的 loopback 端口，不含通道的 ListenAddress/ListenPort），
// 因此普通代理入口必须显式配置。
package relaylink

import (
	"encoding/json"
	"errors"
	"fmt"
	"net"
	"net/http"
	"strings"
	"time"
)

// 稳定错误码。各语言实现必须保持一致语义。
const (
	EntryUnavailable    = "entry_unavailable"
	EntryNotFound       = "entry_not_found"
	TunnelRejected      = "tunnel_rejected"
	TargetConnectFailed = "target_connect_failed"
	Timeout             = "timeout"
	ConfigurationDrift  = "configuration_drift"
	InvalidUsage        = "invalid_usage"
)

const (
	DefaultDashboardPort = 18081
	maxDatagram          = 65507
)

// RelayLinkError 是跨语言一致的错误类型，保留稳定 code 字符串。
type RelayLinkError struct {
	Code    string
	Message string
	Err     error
}

func (e *RelayLinkError) Error() string { return e.Message }
func (e *RelayLinkError) Unwrap() error { return e.Err }

func newError(code, format string, args ...any) *RelayLinkError {
	return &RelayLinkError{Code: code, Message: fmt.Sprintf(format, args...)}
}

// EntryDescriptor 描述一个入口。
type EntryDescriptor struct {
	Host      string
	Port      int
	Transport string
}

// NewEntryDescriptor 校验并创建入口描述。
func NewEntryDescriptor(host string, port int, transport string) (*EntryDescriptor, error) {
	if host == "" {
		return nil, newError(InvalidUsage, "host 不能为空。")
	}
	if port < 1 || port > 65535 {
		return nil, newError(InvalidUsage, "port 必须在 1–65535。")
	}
	if transport == "" {
		transport = "tcp"
	}
	return &EntryDescriptor{Host: host, Port: port, Transport: transport}, nil
}

// Endpoint 返回 host:port 形式。
func (e *EntryDescriptor) Endpoint() string {
	return fmt.Sprintf("%s:%d", e.Host, e.Port)
}

// ConnectTCP 连接 TCP 入口。一次业务流一条连接，支持单向半关闭。
func ConnectTCP(entry *EntryDescriptor, connectTimeout time.Duration) (net.Conn, error) {
	if entry.Transport != "tcp" {
		return nil, newError(InvalidUsage, "该入口不是 TCP。")
	}
	conn, err := net.DialTimeout("tcp", entry.Endpoint(), connectTimeout)
	if err != nil {
		return nil, classifyDialError(err)
	}
	return conn, nil
}

func classifyDialError(err error) *RelayLinkError {
	var dnsErr *net.DNSError
	if errors.As(err, &dnsErr) {
		return &RelayLinkError{Code: EntryNotFound, Message: "无法解析入口主机。", Err: err}
	}
	var netErr net.Error
	if errors.As(err, &netErr) && netErr.Timeout() {
		return &RelayLinkError{Code: Timeout, Message: "连接入口超时。", Err: err}
	}
	message := err.Error()
	if containsConnectionRefused(message) {
		return &RelayLinkError{Code: EntryUnavailable, Message: "入口拒绝连接。", Err: err}
	}
	return &RelayLinkError{Code: TargetConnectFailed, Message: "连接入口失败。", Err: err}
}

func containsConnectionRefused(message string) bool {
	return strings.Contains(message, "connection refused") || strings.Contains(message, "actively refused")
}

// ShutdownWrite 单向半关闭：只关闭发送方向，仍可继续接收对端回包。
func ShutdownWrite(conn net.Conn) error {
	type closeWriter interface{ CloseWrite() error }
	if cw, ok := conn.(closeWriter); ok {
		if err := cw.CloseWrite(); err != nil {
			return &RelayLinkError{Code: TunnelRejected, Message: "半关闭失败。", Err: err}
		}
		return nil
	}
	return &RelayLinkError{Code: TunnelRejected, Message: "该连接不支持单向半关闭。"}
}

// BindUDP 绑定 UDP 入口。单 socket 会话，保留报文边界。
func BindUDP(entry *EntryDescriptor) (*net.UDPConn, error) {
	if entry.Transport != "udp" {
		return nil, newError(InvalidUsage, "该入口不是 UDP。")
	}
	address, err := net.ResolveUDPAddr("udp", entry.Endpoint())
	if err != nil {
		return nil, &RelayLinkError{Code: EntryNotFound, Message: "无法解析 UDP 入口。", Err: err}
	}
	conn, err := net.DialUDP("udp", nil, address)
	if err != nil {
		return nil, &RelayLinkError{Code: EntryUnavailable, Message: "绑定 UDP 入口失败。", Err: err}
	}
	return conn, nil
}

// SendUDP 发送报文。超限时明确报错，不静默截断。
func SendUDP(conn *net.UDPConn, payload []byte) error {
	if len(payload) > maxDatagram {
		return newError(InvalidUsage, "报文超过 UDP 上限，SDK 不静默截断。")
	}
	_, err := conn.Write(payload)
	if err != nil {
		return &RelayLinkError{Code: TargetConnectFailed, Message: "发送 UDP 报文失败。", Err: err}
	}
	return nil
}

type agentSnapshot struct {
	Servers []struct {
		Mappings []struct {
			TargetClientID  string  `json:"targetClientId"`
			TargetChannelID string  `json:"targetChannelId"`
			LocalAddress    *string `json:"localAddress"`
		} `json:"mappings"`
	} `json:"servers"`
}

// DiscoverMapping 从本机 Agent 的 v2 只读 API 发现授权互访入口。
// 只覆盖授权互访；普通代理入口必须显式配置。
func DiscoverMapping(targetClientID, targetChannelID string, dashboardPort int) (*EntryDescriptor, error) {
	if dashboardPort < 1 || dashboardPort > 65535 {
		return nil, newError(InvalidUsage, "dashboardPort 无效。")
	}

	client := &http.Client{Timeout: 5 * time.Second}
	url := fmt.Sprintf("http://127.0.0.1:%d/api/v2/mappings", dashboardPort)
	response, err := client.Get(url)
	if err != nil {
		return nil, &RelayLinkError{Code: EntryUnavailable, Message: "无法连接本机 Agent 只读 API。", Err: err}
	}
	defer response.Body.Close()

	var snapshot agentSnapshot
	if err := json.NewDecoder(response.Body).Decode(&snapshot); err != nil {
		return nil, &RelayLinkError{Code: ConfigurationDrift, Message: "Agent 返回了无法解析的内容。", Err: err}
	}

	for _, server := range snapshot.Servers {
		for _, mapping := range server.Mappings {
			if mapping.TargetClientID != targetClientID || mapping.TargetChannelID != targetChannelID {
				continue
			}
			if mapping.LocalAddress == nil || *mapping.LocalAddress == "" {
				continue
			}
			host, portText, err := net.SplitHostPort(*mapping.LocalAddress)
			if err != nil {
				continue
			}
			var port int
			if _, err := fmt.Sscanf(portText, "%d", &port); err != nil {
				continue
			}
			return NewEntryDescriptor(host, port, "tcp")
		}
	}

	return nil, newError(EntryNotFound, "本机 Agent 没有匹配的授权互访入口。")
}
