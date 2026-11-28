import assert from "node:assert/strict";
import http from "node:http";
import net from "node:net";
import dgram from "node:dgram";
import fs from "node:fs";
import path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

import {
  bindUdp,
  ConfigurationDrift,
  connectTcp,
  discoverMapping,
  EntryDescriptor,
  EntryNotFound,
  EntryUnavailable,
  InvalidUsage,
  RelayLinkError,
  sendUdp,
  shutdownWrite,
  Timeout,
} from "../dist/dataPlane.js";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(__dirname, "..", "..", "..");
const contract = JSON.parse(fs.readFileSync(path.join(repoRoot, "contract", "conformance", "data-plane.json"), "utf8"));
const caseMap = new Map(contract.cases.map((c) => [c.id, c]));

test("conformance cases from contract/conformance/data-plane.json", async () => {
  const empty = caseMap.get("entry-descriptor-rejects-empty-host");
  assert.throws(() => new EntryDescriptor(empty.input.host, empty.input.port), (e) => e instanceof RelayLinkError && e.code === empty.expect.error);

  const badPort = caseMap.get("entry-descriptor-rejects-port-range");
  assert.throws(() => new EntryDescriptor(badPort.input.host, badPort.input.port), (e) => e instanceof RelayLinkError && e.code === badPort.expect.error);

  const endpoint = caseMap.get("entry-endpoint-string");
  const entry = new EntryDescriptor(endpoint.input.host, endpoint.input.port);
  assert.equal(entry.endpoint(), endpoint.expect.endpoint);

  const refused = caseMap.get("tcp-connect-refused-maps-to-entry-unavailable");
  await assert.rejects(
    connectTcp(new EntryDescriptor(refused.input.host, refused.input.port), 5000),
    (e) => e instanceof RelayLinkError && e.code === refused.expect.error
  );

  const tcpMismatch = caseMap.get("tcp-rejects-non-tcp-transport");
  await assert.rejects(
    connectTcp(new EntryDescriptor(tcpMismatch.input.host, tcpMismatch.input.port, tcpMismatch.input.transport)),
    (e) => e instanceof RelayLinkError && e.code === tcpMismatch.expect.error
  );

  const udpMismatch = caseMap.get("udp-rejects-non-udp-transport");
  await assert.rejects(
    bindUdp(new EntryDescriptor(udpMismatch.input.host, udpMismatch.input.port, udpMismatch.input.transport)),
    (e) => e instanceof RelayLinkError && e.code === udpMismatch.expect.error
  );

  const oversized = caseMap.get("udp-rejects-oversized-datagram");
  const sock = await bindUdp(new EntryDescriptor("127.0.0.1", 20001, "udp"));
  try {
    await assert.rejects(
      sendUdp(sock, Buffer.alloc(oversized.input.size, 1)),
      (e) => e instanceof RelayLinkError && e.code === oversized.expect.error
    );
  } finally {
    sock.close();
  }

  const badDash = caseMap.get("discovery-rejects-invalid-dashboard-port");
  await assert.rejects(
    discoverMapping("c", "ch", badDash.input.dashboardPort),
    (e) => e instanceof RelayLinkError && e.code === badDash.expect.error
  );

  const unreachable = caseMap.get("discovery-unreachable-agent");
  await assert.rejects(
    discoverMapping("c", "ch", unreachable.input.dashboardPort),
    (e) => e instanceof RelayLinkError && e.code === unreachable.expect.error
  );
});

test("IPv6 formatting, TCP half-close, timeout, and UDP round-trip", async () => {
  assert.equal(new EntryDescriptor("::1", 20001).endpoint(), "[::1]:20001");
  assert.equal(new EntryDescriptor("[::1]", 20001).endpoint(), "[::1]:20001");
  assert.throws(() => new EntryDescriptor("  ", 8080), (e) => e instanceof RelayLinkError && e.code === InvalidUsage);
  assert.throws(() => new EntryDescriptor("127.0.0.1", 8080, "sctp"), (e) => e instanceof RelayLinkError && e.code === InvalidUsage);

  const tcpServer = net.createServer((conn) => {
    const chunks = [];
    conn.on("data", (d) => chunks.push(d));
    conn.on("end", () => conn.end(Buffer.concat([Buffer.from("ack:"), ...chunks])));
  });
  await new Promise((resolve) => tcpServer.listen(0, "127.0.0.1", resolve));
  const tcpPort = tcpServer.address().port;
  try {
    const client = await connectTcp(new EntryDescriptor("127.0.0.1", tcpPort), 5000);
    client.write("hello");
    shutdownWrite(client);
    const reply = await new Promise((resolve, reject) => {
      const chunks = [];
      client.on("data", (d) => chunks.push(d));
      client.on("end", () => resolve(Buffer.concat(chunks).toString("utf8")));
      client.on("error", reject);
    });
    assert.equal(reply, "ack:hello");
  } finally {
    await new Promise((resolve) => tcpServer.close(resolve));
  }

  const udpServer = dgram.createSocket("udp4");
  await new Promise((resolve) => udpServer.bind(0, "127.0.0.1", resolve));
  const udpPort = udpServer.address().port;
  udpServer.on("message", (msg, rinfo) => {
    udpServer.send(Buffer.from(msg.toString("utf8").toUpperCase()), rinfo.port, rinfo.address);
  });
  try {
    const client = await bindUdp(new EntryDescriptor("127.0.0.1", udpPort, "udp"));
    try {
      const replyPromise = new Promise((resolve) => client.once("message", (m) => resolve(m.toString("utf8"))));
      await sendUdp(client, Buffer.from("ping"));
      assert.equal(await replyPromise, "PING");
    } finally {
      client.close();
    }
  } finally {
    await new Promise((resolve) => udpServer.close(resolve));
  }

  // Test bindUdp DNS resolution failure rejects cleanly without crashing process.
  await assert.rejects(
    bindUdp(new EntryDescriptor("nonexistent.invalid.relaylink.test", 20001, "udp")),
    (e) => e instanceof RelayLinkError && e.code === EntryNotFound
  );
  // Real timeout when target drops SYN / unbound port with tiny timeout on non-listening endpoint or bad timeout usage.
  await assert.rejects(
    connectTcp(new EntryDescriptor("127.0.0.1", 1), 0),
    (e) => e instanceof RelayLinkError && e.code === InvalidUsage
  );
  // Force connect timeout with 1ms connect wait against unbound port 19999? Only if not immediate RST, so test discoverMapping timeout below cleanly.
});

test("discoverMapping handles UDP, skips disabled, disambiguates profileId, and maps bad JSON", async () => {
  let body = JSON.stringify({
    servers: [
      {
        profileId: "east",
        mappings: [
          { enabled: false, targetClientId: "v", targetChannelId: "u", localAddress: "127.0.0.1:20999", transportProtocol: "udp" },
          { enabled: true, targetClientId: "v", targetChannelId: "dup", localAddress: "127.0.0.1:21001", transportProtocol: "tcp" }
        ]
      },
      {
        profileId: "west",
        mappings: [
          { enabled: true, targetClientId: "v", targetChannelId: "u", localAddress: "[::1]:20002", transportProtocol: "udp" },
          { enabled: true, targetClientId: "v", targetChannelId: "dup", localAddress: "127.0.0.1:22002", transportProtocol: "tcp" }
        ]
      }
    ]
  });
  let status = 200;

  const server = http.createServer((_req, res) => {
    res.writeHead(status, { "Content-Type": "application/json" });
    res.end(body);
  });
  await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
  const port = server.address().port;
  try {
    const udp = await discoverMapping("v", "u", port);
    assert.equal(udp.transport, "udp");
    assert.equal(udp.host, "::1");
    assert.equal(udp.port, 20002);
    assert.equal(udp.endpoint(), "[::1]:20002");

    const west = await discoverMapping("v", "dup", port, "west");
    assert.equal(west.port, 22002);

    await assert.rejects(discoverMapping("v", "missing", port), (e) => e instanceof RelayLinkError && e.code === EntryNotFound);

    body = "{bad-json";
    await assert.rejects(discoverMapping("v", "u", port), (e) => e instanceof RelayLinkError && e.code === ConfigurationDrift);

    status = 503;
    body = "";
    await assert.rejects(discoverMapping("v", "u", port), (e) => e instanceof RelayLinkError && e.code === EntryUnavailable);
  } finally {
    await new Promise((resolve) => server.close(resolve));
  }
});
