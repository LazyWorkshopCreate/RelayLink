package com.relaylink.sdk;

import java.io.IOException;
import java.net.Proxy;
import java.net.ProxySelector;
import java.net.SocketAddress;
import java.net.URI;
import java.net.http.HttpClient;
import java.net.http.HttpRequest;
import java.net.http.HttpResponse;
import java.net.http.HttpTimeoutException;
import java.time.Duration;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

/**
 * 从本机 Agent 的 v2 只读 API 发现授权互访入口。
 *
 * <p>只覆盖授权互访；普通代理入口必须显式配置，因为 Agent 视图不含
 * 通道的 ListenAddress/ListenPort。
 */
public final class EntryDiscovery {

    private static final ProxySelector NO_PROXY = new ProxySelector() {
        @Override
        public List<Proxy> select(URI uri) {
            return List.of(Proxy.NO_PROXY);
        }

        @Override
        public void connectFailed(URI uri, SocketAddress sa, IOException ioe) {
        }
    };

    private EntryDiscovery() {
    }

    public static EntryDescriptor discoverMapping(String targetClientId, String targetChannelId, int dashboardPort) {
        return discoverMapping(targetClientId, targetChannelId, dashboardPort, null);
    }

    public static EntryDescriptor discoverMapping(String targetClientId, String targetChannelId, int dashboardPort, String profileId) {
        if (dashboardPort < 1 || dashboardPort > 65535) {
            throw new RelayLinkException(RelayLinkException.INVALID_USAGE, "dashboardPort 无效。");
        }
        if (targetClientId == null || targetClientId.isEmpty() || targetChannelId == null || targetChannelId.isEmpty()) {
            throw new RelayLinkException(RelayLinkException.INVALID_USAGE, "targetClientId 与 targetChannelId 不能为空。");
        }

        HttpClient client = HttpClient.newBuilder()
                .connectTimeout(Duration.ofSeconds(5))
                .proxy(NO_PROXY)
                .followRedirects(HttpClient.Redirect.NEVER)
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
        } catch (HttpTimeoutException exception) {
            throw new RelayLinkException(RelayLinkException.TIMEOUT, "查询本机 Agent 只读 API 超时。", exception);
        } catch (IOException exception) {
            throw new RelayLinkException(RelayLinkException.ENTRY_UNAVAILABLE, "无法连接本机 Agent 只读 API。", exception);
        } catch (InterruptedException exception) {
            Thread.currentThread().interrupt();
            throw new RelayLinkException(RelayLinkException.TIMEOUT, "查询本机 Agent 只读 API 被中断。", exception);
        }

        if (body == null || body.isBlank()) {
            throw new RelayLinkException(RelayLinkException.CONFIGURATION_DRIFT, "Agent 返回了空内容。");
        }

        Object parsed;
        try {
            parsed = new JsonParser(body).parse();
        } catch (IllegalArgumentException exception) {
            throw new RelayLinkException(RelayLinkException.CONFIGURATION_DRIFT, "Agent 返回了无法解析的内容。", exception);
        }

        if (!(parsed instanceof Map<?, ?> root)) {
            throw new RelayLinkException(RelayLinkException.ENTRY_NOT_FOUND, "Agent 未返回 servers。");
        }
        Object serversObj = root.get("servers");
        if (!(serversObj instanceof List<?> servers)) {
            throw new RelayLinkException(RelayLinkException.ENTRY_NOT_FOUND, "Agent 未返回 servers。");
        }

        for (Object serverObj : servers) {
            if (!(serverObj instanceof Map<?, ?> server)) {
                continue;
            }
            if (profileId != null && !profileId.isEmpty() && !profileId.equals(server.get("profileId"))) {
                continue;
            }
            Object mappingsObj = server.get("mappings");
            if (!(mappingsObj instanceof List<?> mappings)) {
                continue;
            }
            for (Object mappingObj : mappings) {
                if (!(mappingObj instanceof Map<?, ?> mapping)) {
                    continue;
                }
                if (Boolean.FALSE.equals(mapping.get("enabled"))) {
                    continue;
                }
                if (!targetClientId.equals(mapping.get("targetClientId"))) {
                    continue;
                }
                if (!targetChannelId.equals(mapping.get("targetChannelId"))) {
                    continue;
                }
                Object localObj = mapping.get("localAddress");
                if (!(localObj instanceof String address) || address.isEmpty()) {
                    continue;
                }
                EndpointParts endpoint = parseEndpoint(address);
                if (endpoint == null) {
                    continue;
                }
                Object transportObj = mapping.get("transportProtocol");
                String transport = (transportObj instanceof String s && !s.isEmpty()) ? s : "tcp";
                return EntryDescriptor.of(endpoint.host(), endpoint.port(), transport);
            }
        }

        throw new RelayLinkException(RelayLinkException.ENTRY_NOT_FOUND, "本机 Agent 没有匹配的授权互访入口。");
    }

    private record EndpointParts(String host, int port) {
    }

    private static EndpointParts parseEndpoint(String value) {
        String trimmed = value.trim();
        String host;
        String portText;
        if (trimmed.startsWith("[")) {
            int close = trimmed.indexOf(']');
            if (close <= 1 || close + 2 >= trimmed.length() || trimmed.charAt(close + 1) != ':') {
                return null;
            }
            host = trimmed.substring(1, close);
            portText = trimmed.substring(close + 2);
        } else {
            int sep = trimmed.lastIndexOf(':');
            if (sep <= 0 || trimmed.indexOf(':') != sep) {
                return null;
            }
            host = trimmed.substring(0, sep);
            portText = trimmed.substring(sep + 1);
        }
        try {
            int port = Integer.parseInt(portText);
            if (port < 1 || port > 65535 || host.isEmpty()) {
                return null;
            }
            return new EndpointParts(host, port);
        } catch (NumberFormatException exception) {
            return null;
        }
    }

    private static final class JsonParser {
        private final String input;
        private int pos;

        JsonParser(String input) {
            this.input = input;
        }

        Object parse() {
            Object val = readValue();
            skipWhitespace();
            if (pos != input.length()) {
                throw new IllegalArgumentException("Trailing JSON content.");
            }
            return val;
        }

        private Object readValue() {
            skipWhitespace();
            if (pos >= input.length()) {
                throw new IllegalArgumentException("Unexpected end of JSON.");
            }
            char c = input.charAt(pos);
            return switch (c) {
                case '{' -> readObject();
                case '[' -> readArray();
                case '"' -> readString();
                case 't' -> readLiteral("true", Boolean.TRUE);
                case 'f' -> readLiteral("false", Boolean.FALSE);
                case 'n' -> readLiteral("null", null);
                default -> readNumber();
            };
        }

        private Map<String, Object> readObject() {
            pos++; // {
            skipWhitespace();
            Map<String, Object> map = new LinkedHashMap<>();
            if (pos < input.length() && input.charAt(pos) == '}') {
                pos++;
                return map;
            }
            while (true) {
                skipWhitespace();
                if (pos >= input.length() || input.charAt(pos) != '"') {
                    throw new IllegalArgumentException("Expected object key.");
                }
                String key = readString();
                skipWhitespace();
                if (pos >= input.length() || input.charAt(pos) != ':') {
                    throw new IllegalArgumentException("Expected ':' after key.");
                }
                pos++;
                map.put(key, readValue());
                skipWhitespace();
                if (pos >= input.length()) {
                    throw new IllegalArgumentException("Unterminated object.");
                }
                char next = input.charAt(pos++);
                if (next == '}') {
                    return map;
                }
                if (next != ',') {
                    throw new IllegalArgumentException("Expected ',' or '}'.");
                }
            }
        }

        private List<Object> readArray() {
            pos++; // [
            skipWhitespace();
            List<Object> list = new ArrayList<>();
            if (pos < input.length() && input.charAt(pos) == ']') {
                pos++;
                return list;
            }
            while (true) {
                list.add(readValue());
                skipWhitespace();
                if (pos >= input.length()) {
                    throw new IllegalArgumentException("Unterminated array.");
                }
                char next = input.charAt(pos++);
                if (next == ']') {
                    return list;
                }
                if (next != ',') {
                    throw new IllegalArgumentException("Expected ',' or ']'.");
                }
            }
        }

        private String readString() {
            pos++; // "
            StringBuilder sb = new StringBuilder();
            while (pos < input.length()) {
                char c = input.charAt(pos++);
                if (c == '"') {
                    return sb.toString();
                }
                if (c < 0x20) {
                    throw new IllegalArgumentException("Control character inside string.");
                }
                if (c == '\\') {
                    if (pos >= input.length()) {
                        throw new IllegalArgumentException("Unterminated escape.");
                    }
                    char esc = input.charAt(pos++);
                    switch (esc) {
                        case '"', '\\', '/' -> sb.append(esc);
                        case 'b' -> sb.append('\b');
                        case 'f' -> sb.append('\f');
                        case 'n' -> sb.append('\n');
                        case 'r' -> sb.append('\r');
                        case 't' -> sb.append('\t');
                        case 'u' -> {
                            if (pos + 4 > input.length()) {
                                throw new IllegalArgumentException("Invalid unicode escape.");
                            }
                            sb.append((char) Integer.parseInt(input.substring(pos, pos + 4), 16));
                            pos += 4;
                        }
                        default -> throw new IllegalArgumentException("Invalid escape sequence.");
                    }
                } else {
                    sb.append(c);
                }
            }
            throw new IllegalArgumentException("Unterminated string.");
        }

        private Object readLiteral(String lit, Object value) {
            if (!input.startsWith(lit, pos)) {
                throw new IllegalArgumentException("Invalid literal.");
            }
            pos += lit.length();
            return value;
        }

        private Number readNumber() {
            int start = pos;
            if (input.charAt(pos) == '-') {
                pos++;
            }
            if (pos >= input.length() || input.charAt(pos) < '0' || input.charAt(pos) > '9') {
                throw new IllegalArgumentException("Invalid number.");
            }
            while (pos < input.length() && Character.isDigit(input.charAt(pos))) {
                pos++;
            }
            boolean isFloat = false;
            if (pos < input.length() && input.charAt(pos) == '.') {
                isFloat = true;
                pos++;
                if (pos >= input.length() || !Character.isDigit(input.charAt(pos))) {
                    throw new IllegalArgumentException("Invalid fraction.");
                }
                while (pos < input.length() && Character.isDigit(input.charAt(pos))) {
                    pos++;
                }
            }
            if (pos < input.length() && (input.charAt(pos) == 'e' || input.charAt(pos) == 'E')) {
                isFloat = true;
                pos++;
                if (pos < input.length() && (input.charAt(pos) == '+' || input.charAt(pos) == '-')) {
                    pos++;
                }
                if (pos >= input.length() || !Character.isDigit(input.charAt(pos))) {
                    throw new IllegalArgumentException("Invalid exponent.");
                }
                while (pos < input.length() && Character.isDigit(input.charAt(pos))) {
                    pos++;
                }
            }
            String num = input.substring(start, pos);
            return isFloat ? Double.parseDouble(num) : Long.parseLong(num);
        }

        private void skipWhitespace() {
            while (pos < input.length()) {
                char c = input.charAt(pos);
                if (c != ' ' && c != '\t' && c != '\r' && c != '\n') {
                    break;
                }
                pos++;
            }
        }
    }
}
