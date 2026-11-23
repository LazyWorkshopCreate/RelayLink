package com.relaylink.sdk;

import java.io.IOException;
import java.net.DatagramPacket;
import java.net.DatagramSocket;
import java.net.InetSocketAddress;

/** UDP 数据面：单 socket 会话，保留报文边界。 */
public final class UdpEntry {

    private static final int MAX_DATAGRAM = 65507;

    private UdpEntry() {
    }

    public static DatagramSocket bind(EntryDescriptor entry) {
        if (!"udp".equals(entry.transport())) {
            throw new RelayLinkException(RelayLinkException.INVALID_USAGE, "该入口不是 UDP。");
        }

        DatagramSocket socket = null;
        try {
            // InetSocketAddress 的主机解析失败以 IAE（未受检）抛出，统一转为 entry_not_found。
            var address = new InetSocketAddress(entry.host(), entry.port());
            socket = new DatagramSocket();
            socket.connect(address);
            return socket;
        } catch (IOException exception) {
            closeQuietly(socket);
            throw new RelayLinkException(RelayLinkException.ENTRY_UNAVAILABLE, "绑定 UDP 入口失败。", exception);
        } catch (IllegalArgumentException exception) {
            closeQuietly(socket);
            throw new RelayLinkException(RelayLinkException.ENTRY_NOT_FOUND, "无法解析 UDP 入口。", exception);
        }
    }

    /** 发送报文。超限时明确报错，不静默截断。 */
    public static void send(DatagramSocket socket, byte[] payload) {
        if (payload.length > MAX_DATAGRAM) {
            throw new RelayLinkException(RelayLinkException.INVALID_USAGE, "报文超过 UDP 上限，SDK 不静默截断。");
        }
        try {
            socket.send(new DatagramPacket(payload, payload.length));
        } catch (IOException exception) {
            throw new RelayLinkException(RelayLinkException.TARGET_CONNECT_FAILED, "发送 UDP 报文失败。", exception);
        }
    }

    private static void closeQuietly(DatagramSocket socket) {
        if (socket != null) {
            socket.close();
        }
    }
}
