package com.relaylink.sdk;

import java.io.IOException;
import java.net.URI;
import java.net.http.HttpClient;
import java.net.http.HttpRequest;
import java.net.http.HttpResponse;
import java.time.Duration;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

/**
 * 从本机 Agent 的 v2 只读 API 发现授权互访入口。
 *
 * <p>只覆盖授权互访；普通代理入口必须显式配置，因为 Agent 视图不含
 * 通道的 ListenAddress/ListenPort。
 *
 * <p><b>已知限制：</b>JDK 没有内置 JSON 解析器，而本 SDK 承诺「仅用 JDK」。
 * 这里用受限正则提取映射三元组，只在字段顺序固定时可靠。若 Agent 的 JSON
 * 序列化顺序变化，发现可能失效——此时会明确报 entry_not_found，而不是返回
 * 错误地址。需要稳健解析时应让调用方改用显式入口，或自行引入 JSON 库。
 */
public final class EntryDiscovery {

    private static final Pattern MAPPING =
            Pattern.compile("\"targetClientId\"\\s*:\\s*\"([^\"]*)\"[^}]*?\"targetChannelId\"\\s*:\\s*\"([^\"]*)\"[^}]*?\"localAddress\"\\s*:\\s*\"([0-9.]+):(\\d+)\"");

    private EntryDiscovery() {
    }

    public static EntryDescriptor discoverMapping(String targetClientId, String targetChannelId, int dashboardPort) {
        if (dashboardPort < 1 || dashboardPort > 65535) {
            throw new RelayLinkException(RelayLinkException.INVALID_USAGE, "dashboardPort 无效。");
        }

        HttpClient client = HttpClient.newBuilder()
                .connectTimeout(Duration.ofSeconds(5))
                .build();
        HttpRequest request = HttpRequest.newBuilder()
                .uri(URI.create("http://127.0.0.1:" + dashboardPort + "/api/v2/mappings"))
                .timeout(Duration.ofSeconds(5))
                .GET()
                .build();

        String body;
        try {
            HttpResponse<String> response = client.send(request, HttpResponse.BodyHandlers.ofString());
            if (response.statusCode() != 200) {
                throw new RelayLinkException(RelayLinkException.ENTRY_UNAVAILABLE,
                        "Agent 只读 API 返回 " + response.statusCode() + "。");
            }
            body = response.body();
        } catch (IOException exception) {
            throw new RelayLinkException(RelayLinkException.ENTRY_UNAVAILABLE, "无法连接本机 Agent 只读 API。", exception);
        } catch (InterruptedException exception) {
            Thread.currentThread().interrupt();
            throw new RelayLinkException(RelayLinkException.TIMEOUT, "查询本机 Agent 只读 API 被中断。", exception);
        }

        if (body == null) {
            throw new RelayLinkException(RelayLinkException.CONFIGURATION_DRIFT, "Agent 返回了空内容。");
        }

        Matcher matcher = MAPPING.matcher(body);
        while (matcher.find()) {
            if (!targetClientId.equals(matcher.group(1))) {
                continue;
            }
            if (!targetChannelId.equals(matcher.group(2))) {
                continue;
            }
            String host = matcher.group(3);
            int port;
            try {
                port = Integer.parseInt(matcher.group(4));
            } catch (NumberFormatException exception) {
                continue;
            }
            return EntryDescriptor.of(host, port);
        }

        throw new RelayLinkException(RelayLinkException.ENTRY_NOT_FOUND, "本机 Agent 没有匹配的授权互访入口。");
    }
}
