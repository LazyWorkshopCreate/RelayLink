package com.relaylink.sdk;

import java.io.IOException;
import java.net.InetSocketAddress;
import java.net.Socket;
import java.net.SocketTimeoutException;
import java.net.UnknownHostException;

/** TCP 数据面：一次业务流一条连接，支持单向半关闭。 */
public final class TcpEntry {

    private TcpEntry() {
    }

    public static Socket connect(EntryDescriptor entry, int connectTimeoutMillis) {
        if (!"tcp".equals(entry.transport())) {
            throw new RelayLinkException(RelayLinkException.INVALID_USAGE, "该入口不是 TCP。");
        }

        Socket socket = new Socket();
        try {
            socket.connect(new InetSocketAddress(entry.host(), entry.port()), connectTimeoutMillis);
            return socket;
        } catch (SocketTimeoutException exception) {
            closeQuietly(socket);
            throw new RelayLinkException(RelayLinkException.TIMEOUT, "连接入口超时。", exception);
        } catch (UnknownHostException exception) {
            closeQuietly(socket);
            throw new RelayLinkException(RelayLinkException.ENTRY_NOT_FOUND, "无法解析入口主机。", exception);
        } catch (IOException exception) {
            closeQuietly(socket);
            String message = exception.getMessage() == null ? "" : exception.getMessage();
            String code = message.contains("refused")
                    ? RelayLinkException.ENTRY_UNAVAILABLE
                    : RelayLinkException.TARGET_CONNECT_FAILED;
            throw new RelayLinkException(code, "连接入口失败：" + message, exception);
        }
    }

    /** 单向半关闭：只关闭发送方向，仍可继续接收对端回包。 */
    public static void shutdownWrite(Socket socket) {
        try {
            socket.shutdownOutput();
        } catch (IOException exception) {
            throw new RelayLinkException(RelayLinkException.TUNNEL_REJECTED, "半关闭失败：" + exception.getMessage(), exception);
        }
    }

    private static void closeQuietly(Socket socket) {
        try {
            socket.close();
        } catch (IOException ignored) {
            // 关闭失败不掩盖原始错误。
        }
    }
}
