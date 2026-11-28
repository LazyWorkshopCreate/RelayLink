package com.relaylink.sdk;

import com.sun.net.httpserver.HttpServer;

import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.net.DatagramPacket;
import java.net.DatagramSocket;
import java.net.InetAddress;
import java.net.InetSocketAddress;
import java.net.ServerSocket;
import java.net.Socket;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.concurrent.atomic.AtomicReference;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

/**
 * Shared conformance + end-to-end verification suite for Java SDK.
 * Runs standalone via `java ... ConformanceRunner` with zero external test dependencies,
 * and can also be invoked by JUnit {@link ConformanceTest}.
 */
public final class ConformanceRunner {

    public static void main(String[] args) throws Exception {
        runAll();
        System.out.println("Java SDK conformance passed: all cases verified.");
    }

    public static void runAll() throws Exception {
        String json = readConformanceJson();
        verifyEntryCases(json);
        verifyTcpRefusedAndMismatch(json);
        verifyUdpCases(json);
        verifyDiscoveryCases(json);
        verifyHalfCloseAndUdpRoundtrip();
        verifyMockHttpDiscovery();
    }

    private static void verifyEntryCases(String json) {
        String emptyHost = stringField(json, "entry-descriptor-rejects-empty-host", "host");
        int emptyPort = intField(json, "entry-descriptor-rejects-empty-host", "port");
        assertError(RelayLinkException.INVALID_USAGE, () -> EntryDescriptor.of(emptyHost, emptyPort));

        String badHost = stringField(json, "entry-descriptor-rejects-port-range", "host");
        int badPort = intField(json, "entry-descriptor-rejects-port-range", "port");
        assertError(RelayLinkException.INVALID_USAGE, () -> EntryDescriptor.of(badHost, badPort));

        String host = stringField(json, "entry-endpoint-string", "host");
        int port = intField(json, "entry-endpoint-string", "port");
        String expected = stringField(json, "entry-endpoint-string", "endpoint");
        assertEq(expected, EntryDescriptor.of(host, port).endpoint(), "endpoint");

        assertEq("[::1]:20001", EntryDescriptor.of("::1", 20001).endpoint(), "ipv6 endpoint");
        assertEq("[::1]:20001", EntryDescriptor.of("[::1]", 20001).endpoint(), "ipv6 bracket endpoint");
        assertError(RelayLinkException.INVALID_USAGE, () -> EntryDescriptor.of("   ", 8080));
        assertError(RelayLinkException.INVALID_USAGE, () -> EntryDescriptor.of("127.0.0.1", 8080, "sctp"));
    }

    private static void verifyTcpRefusedAndMismatch(String json) {
        String refusedHost = stringField(json, "tcp-connect-refused-maps-to-entry-unavailable", "host");
        int refusedPort = intField(json, "tcp-connect-refused-maps-to-entry-unavailable", "port");
        String refusedErr = stringField(json, "tcp-connect-refused-maps-to-entry-unavailable", "error");
        assertError(refusedErr, () -> TcpEntry.connect(EntryDescriptor.of(refusedHost, refusedPort), 5000));

        String th = stringField(json, "tcp-rejects-non-tcp-transport", "host");
        int tp = intField(json, "tcp-rejects-non-tcp-transport", "port");
        String tt = stringField(json, "tcp-rejects-non-tcp-transport", "transport");
        String te = stringField(json, "tcp-rejects-non-tcp-transport", "error");
        assertError(te, () -> TcpEntry.connect(EntryDescriptor.of(th, tp, tt), 1000));
    }

    private static void verifyUdpCases(String json) {
        String uh = stringField(json, "udp-rejects-non-udp-transport", "host");
        int up = intField(json, "udp-rejects-non-udp-transport", "port");
        String ut = stringField(json, "udp-rejects-non-udp-transport", "transport");
        String ue = stringField(json, "udp-rejects-non-udp-transport", "error");
        assertError(ue, () -> UdpEntry.bind(EntryDescriptor.of(uh, up, ut)));

        int size = intField(json, "udp-rejects-oversized-datagram", "size");
        String oe = stringField(json, "udp-rejects-oversized-datagram", "error");
        try (DatagramSocket sock = UdpEntry.bind(EntryDescriptor.of("127.0.0.1", 20001, "udp"))) {
            assertError(oe, () -> UdpEntry.send(sock, new byte[size]));
        }
    }

    private static void verifyDiscoveryCases(String json) {
        int badPort = intField(json, "discovery-rejects-invalid-dashboard-port", "dashboardPort");
        String badErr = stringField(json, "discovery-rejects-invalid-dashboard-port", "error");
        assertError(badErr, () -> EntryDiscovery.discoverMapping("c", "ch", badPort));

        int unreachPort = intField(json, "discovery-unreachable-agent", "dashboardPort");
        String unreachErr = stringField(json, "discovery-unreachable-agent", "error");
        assertError(unreachErr, () -> EntryDiscovery.discoverMapping("c", "ch", unreachPort));
    }

    private static void verifyHalfCloseAndUdpRoundtrip() throws Exception {
        try (ServerSocket server = new ServerSocket(0, 1, InetAddress.getLoopbackAddress())) {
            int port = server.getLocalPort();
            Thread t = new Thread(() -> {
                try (Socket conn = server.accept()) {
                    byte[] req = conn.getInputStream().readAllBytes();
                    ByteArrayOutputStream out = new ByteArrayOutputStream();
                    out.write("ok:".getBytes(StandardCharsets.UTF_8));
                    out.write(req);
                    conn.getOutputStream().write(out.toByteArray());
                } catch (IOException ignored) {
                }
            });
            t.setDaemon(true);
            t.start();

            try (Socket client = TcpEntry.connect(EntryDescriptor.of("127.0.0.1", port), 5000)) {
                client.getOutputStream().write("ping".getBytes(StandardCharsets.UTF_8));
                TcpEntry.shutdownWrite(client);
                String reply = new String(client.getInputStream().readAllBytes(), StandardCharsets.UTF_8);
                assertEq("ok:ping", reply, "half-close reply");
            }
        }

        try (DatagramSocket udpServer = new DatagramSocket(0, InetAddress.getLoopbackAddress())) {
            int udpPort = udpServer.getLocalPort();
            Thread t = new Thread(() -> {
                try {
                    byte[] buf = new byte[128];
                    DatagramPacket pkt = new DatagramPacket(buf, buf.length);
                    udpServer.receive(pkt);
                    byte[] upper = new String(pkt.getData(), 0, pkt.getLength(), StandardCharsets.UTF_8)
                            .toUpperCase().getBytes(StandardCharsets.UTF_8);
                    udpServer.send(new DatagramPacket(upper, upper.length, pkt.getSocketAddress()));
                } catch (IOException ignored) {
                }
            });
            t.setDaemon(true);
            t.start();

            try (DatagramSocket udpClient = UdpEntry.bind(EntryDescriptor.of("127.0.0.1", udpPort, "udp"))) {
                udpClient.setSoTimeout(2000);
                UdpEntry.send(udpClient, "ping".getBytes(StandardCharsets.UTF_8));
                byte[] buf = new byte[64];
                DatagramPacket resp = new DatagramPacket(buf, buf.length);
                udpClient.receive(resp);
                assertEq("PING", new String(resp.getData(), 0, resp.getLength(), StandardCharsets.UTF_8), "udp roundtrip");
            }
        }
    }

    private static void verifyMockHttpDiscovery() throws Exception {
        String validJson = """
            {
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
            }
            """;
        AtomicReference<String> bodyRef = new AtomicReference<>(validJson);
        HttpServer server = HttpServer.create(new InetSocketAddress(InetAddress.getLoopbackAddress(), 0), 0);
        server.createContext("/api/v2/mappings", exchange -> {
            byte[] bytes = bodyRef.get().getBytes(StandardCharsets.UTF_8);
            exchange.getResponseHeaders().add("Content-Type", "application/json");
            exchange.sendResponseHeaders(200, bytes.length);
            exchange.getResponseBody().write(bytes);
            exchange.close();
        });
        server.start();
        try {
            int port = server.getAddress().getPort();
            EntryDescriptor udp = EntryDiscovery.discoverMapping("v", "u", port);
            assertEq("udp", udp.transport(), "udp transport");
            assertEq("::1", udp.host(), "ipv6 host");
            assertEq("[::1]:20002", udp.endpoint(), "ipv6 discovery endpoint");

            EntryDescriptor west = EntryDiscovery.discoverMapping("v", "dup", port, "west");
            if (west.port() != 22002) {
                throw new AssertionError("Expected port 22002 for west profile, got " + west.port());
            }

            assertError(RelayLinkException.ENTRY_NOT_FOUND, () -> EntryDiscovery.discoverMapping("v", "missing", port));

            bodyRef.set("{corrupt");
            assertError(RelayLinkException.CONFIGURATION_DRIFT, () -> EntryDiscovery.discoverMapping("v", "u", port));
        } finally {
            server.stop(0);
        }
    }

    private static void assertError(String expectedCode, Runnable action) {
        try {
            action.run();
        } catch (RelayLinkException ex) {
            assertEq(expectedCode, ex.code(), "error code");
            return;
        }
        throw new AssertionError("Expected RelayLinkException(" + expectedCode + "), but call succeeded.");
    }

    private static void assertEq(String expected, String actual, String label) {
        if (!expected.equals(actual)) {
            throw new AssertionError(label + ": expected '" + expected + "' but got '" + actual + "'");
        }
    }

    private static String readConformanceJson() throws IOException {
        Path cwd = Path.of("").toAbsolutePath();
        while (cwd != null && !Files.exists(cwd.resolve("contract/conformance/data-plane.json"))) {
            cwd = cwd.getParent();
        }
        if (cwd == null) {
            throw new IllegalStateException("contract/conformance/data-plane.json not found above cwd");
        }
        return Files.readString(cwd.resolve("contract/conformance/data-plane.json"), StandardCharsets.UTF_8);
    }

    private static String caseWindow(String json, String caseId) {
        Matcher m = Pattern.compile("\"id\"\\s*:\\s*\"" + Pattern.quote(caseId) + "\"").matcher(json);
        if (!m.find()) {
            throw new IllegalStateException("case " + caseId + " not found");
        }
        return json.substring(m.start(), Math.min(json.length(), m.start() + 800));
    }

    private static int intField(String json, String caseId, String field) {
        Matcher m = Pattern.compile("\"" + Pattern.quote(field) + "\"\\s*:\\s*(-?\\d+)").matcher(caseWindow(json, caseId));
        if (!m.find()) {
            throw new IllegalStateException("field " + field + " not found in " + caseId);
        }
        return Integer.parseInt(m.group(1));
    }

    private static String stringField(String json, String caseId, String field) {
        Matcher m = Pattern.compile("\"" + Pattern.quote(field) + "\"\\s*:\\s*\"([^\"]*)\"").matcher(caseWindow(json, caseId));
        if (!m.find()) {
            throw new IllegalStateException("field " + field + " not found in " + caseId);
        }
        return m.group(1);
    }
}
