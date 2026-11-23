package com.relaylink.sdk;

import java.nio.file.Files;
import java.nio.file.Path;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.*;

/// 一致性用例：读取 contract/conformance/data-plane.json，
/// 保证各语言实现面对同一批场景给出同一批错误码。
class ConformanceTest {

    private static String repoRoot() {
        // build/classes 起点上溯到仓库根
        Path cwd = Path.of("").toAbsolutePath();
        while (cwd != null && !Files.exists(cwd.resolve("contract/conformance/data-plane.json"))) {
            cwd = cwd.getParent();
        }
        if (cwd == null) throw new IllegalStateException("contract/conformance/data-plane.json not found above cwd");
        return cwd.toString();
    }

    private static String readConformanceJson() {
        try {
            return Files.readString(Path.of(repoRoot(), "contract", "conformance", "data-plane.json"));
        } catch (Exception exception) {
            throw new IllegalStateException("无法读取一致性用例文件", exception);
        }
    }

    /// 从 "id":"<caseId>" 起向后取固定窗口再提取字段：用例块含嵌套 input 对象，
    /// 受限正则无法跨嵌套括号；窗口 800 字符足够覆盖单个用例块。
    private static String caseWindow(String json, String caseId) {
        var marker = "\"id\"\\s*:\\s*\"" + caseId + "\"";
        var markerPattern = Pattern.compile(marker);
        java.util.regex.Matcher startMatcher = markerPattern.matcher(json);
        assertTrue(startMatcher.find(), "case " + caseId + " not found");
        int start = startMatcher.start();
        assertTrue(start >= 0, "case " + caseId + " not found");
        return json.substring(start, Math.min(json.length(), start + 800));
    }

    private static int intField(String json, String caseId, String field) {
        var fieldPattern = Pattern.compile("\"" + Pattern.quote(field) + "\"\\s*:\\s*(-?\\d+)");
        Matcher fieldMatcher = fieldPattern.matcher(caseWindow(json, caseId));
        assertTrue(fieldMatcher.find(), "field " + field + " not found in case " + caseId);
        return Integer.parseInt(fieldMatcher.group(1));
    }

    private static String stringField(String json, String caseId, String field) {
        var fieldPattern = Pattern.compile("\"" + Pattern.quote(field) + "\"\\s*:\\s*\"([^\"]*)\"");
        Matcher fieldMatcher = fieldPattern.matcher(caseWindow(json, caseId));
        assertTrue(fieldMatcher.find(), "field " + field + " not found in case " + caseId);
        return fieldMatcher.group(1);
    }

    @Test
    void entryDescriptorRejectsEmptyHost() {
        String json = readConformanceJson();
        String host = stringField(json, "entry-descriptor-rejects-empty-host", "host");
        int port = intField(json, "entry-descriptor-rejects-empty-host", "port");

        RelayLinkException exception = assertThrows(RelayLinkException.class, () -> EntryDescriptor.of(host, port));
        assertEquals(RelayLinkException.INVALID_USAGE, exception.code());
    }

    @Test
    void entryDescriptorRejectsPortRange() {
        String json = readConformanceJson();
        String host = stringField(json, "entry-descriptor-rejects-port-range", "host");
        int port = intField(json, "entry-descriptor-rejects-port-range", "port");

        RelayLinkException exception = assertThrows(RelayLinkException.class, () -> EntryDescriptor.of(host, port));
        assertEquals(RelayLinkException.INVALID_USAGE, exception.code());
    }

    @Test
    void entryEndpointString() {
        String json = readConformanceJson();
        String host = stringField(json, "entry-endpoint-string", "host");
        int port = intField(json, "entry-endpoint-string", "port");
        String expected = stringField(json, "entry-endpoint-string", "endpoint");

        EntryDescriptor entry = EntryDescriptor.of(host, port);
        assertEquals(expected, entry.endpoint());
    }

    @Test
    void tcpConnectRefusedMapsToEntryUnavailable() {
        String json = readConformanceJson();
        String host = stringField(json, "tcp-connect-refused-maps-to-entry-unavailable", "host");
        int port = intField(json, "tcp-connect-refused-maps-to-entry-unavailable", "port");
        String expected = stringField(json, "tcp-connect-refused-maps-to-entry-unavailable", "error");

        EntryDescriptor entry = EntryDescriptor.of(host, port);
        RelayLinkException exception = assertThrows(RelayLinkException.class, () -> TcpEntry.connect(entry, 200));
        assertEquals(expected, exception.code());
    }

    @Test
    void transportMismatchIsRejected() {
        String json = readConformanceJson();
        String host = stringField(json, "tcp-rejects-non-tcp-transport", "host");
        int port = intField(json, "tcp-rejects-non-tcp-transport", "port");
        String transport = stringField(json, "tcp-rejects-non-tcp-transport", "transport");

        EntryDescriptor entry = EntryDescriptor.of(host, port, transport);
        RelayLinkException exception = assertThrows(RelayLinkException.class, () -> TcpEntry.connect(entry, 200));
        assertEquals(RelayLinkException.INVALID_USAGE, exception.code());
    }
}
