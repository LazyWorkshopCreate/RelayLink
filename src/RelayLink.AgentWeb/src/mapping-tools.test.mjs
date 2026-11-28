import assert from "node:assert/strict";
import test from "node:test";
import { mappingTools } from "./mapping-tools.ts";

const raw = {
    enabled: true,
    applicationProtocol: "raw",
    transportProtocol: "tcp",
    localAddress: "127.0.0.1:13389",
};

test("maps a raw TCP entry to native tools with an address-only RDP file", () => {
    assert.deepEqual(mappingTools(raw, true), {
        kind: "raw",
        address: "127.0.0.1:13389",
        sshUrl: "ssh://127.0.0.1:13389",
        sshCommand: "ssh -p 13389 127.0.0.1",
        rdpFile: "full address:s:127.0.0.1:13389\r\n",
    });
    assert.equal(mappingTools(raw, false).rdpFile, null);
});

test("preserves and normalizes the URL for a ready HTTP entry", () => {
    const mapping = {
        ...raw,
        applicationProtocol: "http",
        httpState: "ready",
        accessUrl: "https://panel.example.test:13389/dashboard?view=home",
    };
    assert.deepEqual(mappingTools(mapping, true), {
        kind: "web",
        url: mapping.accessUrl,
    });
    assert.deepEqual(
        mappingTools(
            {
                ...mapping,
                localAddress: "127.0.0.1:443",
                accessUrl: "https://panel.example.test:443",
            },
            true,
        ),
        { kind: "web", url: "https://panel.example.test/" },
    );
    assert.deepEqual(
        mappingTools(
            {
                ...mapping,
                accessUrl: "http://127.0.0.1:13389",
            },
            true,
        ),
        { kind: "web", url: "http://127.0.0.1:13389/" },
    );
});

test("rejects disabled, UDP and non-loopback mappings", () => {
    for (const input of [
        { ...raw, enabled: false },
        { ...raw, transportProtocol: "udp" },
        { ...raw, localAddress: null },
        { ...raw, localAddress: "192.168.1.42:3389" },
        { ...raw, localAddress: "127.0.0.1:0" },
        { ...raw, localAddress: "127.0.0.1:65536" },
        { ...raw, localAddress: "127.0.0.1:3389\n" },
        { ...raw, localAddress: "127.0.0.1:3389\r\nusername:s:another-user" },
    ]) {
        assert.equal(mappingTools(input, true), null);
    }
});

test("rejects unready HTTP, unsafe URLs, control chars, non-loopback IPs and mismatched ports", () => {
    const mapping = {
        ...raw,
        applicationProtocol: "http",
        httpState: "ready",
    };
    for (const input of [
        {
            ...mapping,
            httpState: "pending",
            accessUrl: "https://panel.example.test:13389",
        },
        { ...mapping, accessUrl: "javascript:alert(1)" },
        { ...mapping, accessUrl: "https://user@panel.example.test:13389" },
        { ...mapping, accessUrl: "https://@panel.example.test:13389" },
        { ...mapping, accessUrl: "https://panel.example.test:443" },
        { ...mapping, accessUrl: "//panel.example.test:13389" },
        { ...mapping, accessUrl: "http://panel.example.test:13389/\r\n" },
        { ...mapping, accessUrl: "http://panel.example.test:13389/\t" },
        { ...mapping, accessUrl: "http://192.168.1.10:13389" },
        { ...mapping, accessUrl: "http://[::1]:13389" },
        { ...mapping, accessUrl: "http://0x7f000001:13389" },
        { ...mapping, accessUrl: "http://2130706433:13389" },
        { ...mapping, accessUrl: "http://127.1:13389" },
        { ...mapping, accessUrl: "http://panel.example.test:013389" },
        { ...mapping, accessUrl: "http://panel.example.test:" },
    ]) {
        assert.equal(mappingTools(input, true), null);
    }
});
