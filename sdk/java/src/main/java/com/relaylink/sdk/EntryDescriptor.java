package com.relaylink.sdk;

/**
 * RelayLink SDK —— Java 数据面。
 *
 * <p>SDK 只面向「入口」，不实现中继协议，也不持有任何中继密钥。
 * 仅使用 JDK，不引入第三方依赖。
 *
 * <p>重要限制：普通代理入口无法从本机 Agent 的 v2 只读 API 自动发现
 * （该视图只上报授权互访的 loopback 端口，不含通道的 ListenAddress/ListenPort），
 * 因此普通代理入口必须显式配置。
 */
public final class EntryDescriptor {

    public static final int DEFAULT_DASHBOARD_PORT = 18081;

    private final String host;
    private final int port;
    private final String transport;

    private EntryDescriptor(String host, int port, String transport) {
        this.host = host;
        this.port = port;
        this.transport = transport;
    }

    public static EntryDescriptor of(String host, int port) {
        return of(host, port, "tcp");
    }

    public static EntryDescriptor of(String host, int port, String transport) {
        if (host == null || host.trim().isEmpty()) {
            throw new RelayLinkException(RelayLinkException.INVALID_USAGE, "host 不能为空。");
        }
        if (port < 1 || port > 65535) {
            throw new RelayLinkException(RelayLinkException.INVALID_USAGE, "port 必须在 1–65535。");
        }
        String normalized = (transport == null || transport.isEmpty()) ? "tcp" : transport;
        return new EntryDescriptor(host, port, normalized);
    }

    public String host() {
        return host;
    }

    public int port() {
        return port;
    }

    public String transport() {
        return transport;
    }

    public String endpoint() {
        return host + ":" + port;
    }

    @Override
    public String toString() {
        return "EntryDescriptor(" + endpoint() + ", " + transport + ")";
    }
}
