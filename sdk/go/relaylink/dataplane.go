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
	"io"
	"net"
	"net/http"
	"strconv"
	"strings"
	"syscall"
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
	clean := strings.TrimSpace(host)
	if clean == "" {
		return nil, newError(InvalidUsage, "host 不能为空。")
	}
	if strings.HasPrefix(clean, "[") && strings.HasSuffix(clean, "]") && len(clean) > 2 {
		clean = clean[1 : len(clean)-1]
	}
	if port < 1 || port > 65535 {
		return nil, newError(InvalidUsage, "port 必须在 1–65535。")
	}
	if transport == "" {
		transport = "tcp"
	}
	if transport != "tcp" && transport != "udp" {
		return nil, newError(InvalidUsage, "transport 必须为 tcp 或 udp。")
	}
	return &EntryDescriptor{Host: clean, Port: port, Transport: transport}, nil
}

// Endpoint 返回 host:port 形式（IPv6 自动加方括号）。
func (e *EntryDescriptor) Endpoint() string {
	return net.JoinHostPort(e.Host, strconv.Itoa(e.Port))
}

// ConnectTCP 连接 TCP 入口。一次业务流一条连接，支持单向半关闭。
func ConnectTCP(entry *EntryDescriptor, connectTimeout time.Duration) (net.Conn, error) {
	if entry == nil {
		return nil, newError(InvalidUsage, "entry 不能为空。")
	}
	if entry.Transport != "tcp" {
		return nil, newError(InvalidUsage, "该入口不是 TCP。")
	}
	if connectTimeout <= 0 {
		return nil, newError(InvalidUsage, "connectTimeout 必须大于 0。")
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
	if errors.Is(err, syscall.ECONNREFUSED) || errors.Is(err, syscall.Errno(10061)) || containsConnectionRefused(err.Error()) {
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
	if entry == nil {
		return nil, newError(InvalidUsage, "entry 不能为空。")
	}
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
		ProfileID string `json:"profileId"`
		Mappings  []struct {
			Enabled           *bool   `json:"enabled"`
			TargetClientID    string  `json:"targetClientId"`
			TargetChannelID   string  `json:"targetChannelId"`
			LocalAddress      *string `json:"localAddress"`
			TransportProtocol string  `json:"transportProtocol"`
		} `json:"mappings"`
	} `json:"servers"`
}

// DiscoverMapping 从本机 Agent 的 v2 只读 API 发现授权互访入口。
// 只覆盖授权互访；普通代理入口必须显式配置。
func DiscoverMapping(targetClientID, targetChannelID string, dashboardPort int) (*EntryDescriptor, error) {
	return DiscoverMappingForProfile(targetClientID, targetChannelID, dashboardPort, "")
}

// DiscoverMappingForProfile 按可选 profileID 从本机 Agent 的 v2 只读 API 发现授权互访入口。
func DiscoverMappingForProfile(targetClientID, targetChannelID string, dashboardPort int, profileID string) (*EntryDescriptor, error) {
	if dashboardPort < 1 || dashboardPort > 65535 {
		return nil, newError(InvalidUsage, "dashboardPort 无效。")
	}
	if targetClientID == "" || targetChannelID == "" {
		return nil, newError(InvalidUsage, "targetClientID 与 targetChannelID 不能为空。")
	}

	client := &http.Client{
		Timeout:   5 * time.Second,
		Transport: &http.Transport{Proxy: nil},
		CheckRedirect: func(*http.Request, []*http.Request) error {
			return http.ErrUseLastResponse
		},
	}
	url := fmt.Sprintf("http://127.0.0.1:%d/api/v2/mappings", dashboardPort)
	response, err := client.Get(url)
	if err != nil {
		var netErr net.Error
		if errors.As(err, &netErr) && netErr.Timeout() {
			return nil, &RelayLinkError{Code: Timeout, Message: "查询本机 Agent 只读 API 超时。", Err: err}
		}
		return nil, &RelayLinkError{Code: EntryUnavailable, Message: "无法连接本机 Agent 只读 API。", Err: err}
	}
	defer response.Body.Close()

	if response.StatusCode != http.StatusOK {
		return nil, newError(EntryUnavailable, "Agent 只读 API 返回 %d。", response.StatusCode)
	}

	body, err := io.ReadAll(response.Body)
	if err != nil {
		var netErr net.Error
		if errors.As(err, &netErr) && netErr.Timeout() {
			return nil, &RelayLinkError{Code: Timeout, Message: "读取本机 Agent 响应超时。", Err: err}
		}
		return nil, &RelayLinkError{Code: EntryUnavailable, Message: "读取本机 Agent 响应失败。", Err: err}
	}

	var snapshot agentSnapshot
	if err := json.Unmarshal(body, &snapshot); err != nil {
		return nil, &RelayLinkError{Code: ConfigurationDrift, Message: "Agent 返回了无法解析的内容。", Err: err}
	}

	for _, server := range snapshot.Servers {
		if profileID != "" && server.ProfileID != profileID {
			continue
		}
		for _, mapping := range server.Mappings {
			if mapping.Enabled != nil && !*mapping.Enabled {
				continue
			}
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
			port, err := strconv.Atoi(portText)
			if err != nil || port < 1 || port > 65535 {
				continue
			}
			transport := mapping.TransportProtocol
			if transport == "" {
				transport = "tcp"
			}
			return NewEntryDescriptor(host, port, transport)
		}
	}

	return nil, newError(EntryNotFound, "本机 Agent 没有匹配的授权互访入口。")
}
