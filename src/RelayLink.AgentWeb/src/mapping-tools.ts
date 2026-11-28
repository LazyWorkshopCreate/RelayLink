type MappingEndpoint = {
    enabled: boolean;
    applicationProtocol?: "raw" | "http";
    httpState?: string | null;
    accessUrl?: string | null;
    transportProtocol?: "tcp" | "udp";
    localAddress: string | null;
};

type MappingTools =
    | { kind: "web"; url: string }
    | {
          kind: "raw";
          address: string;
          sshUrl: string;
          sshCommand: string;
          rdpFile: string | null;
      };

export function mappingTools(
    mapping: MappingEndpoint,
    supportsRdp: boolean,
): MappingTools | null {
    if (
        !mapping.enabled ||
        (mapping.transportProtocol ?? "tcp") !== "tcp" ||
        !mapping.localAddress
    )
        return null;

    const endpoint = /^127\.0\.0\.1:([1-9]\d{0,4})$/.exec(mapping.localAddress);
    if (!endpoint || endpoint[0] !== mapping.localAddress) return null;
    const port = Number(endpoint[1]);
    if (port > 65535) return null;

    if (mapping.applicationProtocol === "http") {
        if (mapping.httpState !== "ready" || !mapping.accessUrl) return null;
        if (/[\u0000-\u001f\u007f\s]/.test(mapping.accessUrl)) return null;
        try {
            const authority = /^https?:\/\/([^/?#\\]+)(?:[/?#]|$)/i.exec(
                mapping.accessUrl,
            )?.[1];
            if (
                !authority ||
                authority.includes("@") ||
                authority.includes("[") ||
                authority.includes("]")
            ) {
                return null;
            }

            let hostPart = authority;
            if (authority.includes(":")) {
                const match = /^([^:]+):([1-9]\d{0,4})$/.exec(authority);
                if (!match || Number(match[2]) > 65535) return null;
                hostPart = match[1];
            }

            const url = new URL(mapping.accessUrl);
            if (url.protocol !== "http:" && url.protocol !== "https:")
                return null;

            const isNormalizedIpv4 = /^(?:\d{1,3}\.){3}\d{1,3}$/.test(
                url.hostname,
            );
            if (
                isNormalizedIpv4 &&
                (hostPart !== "127.0.0.1" || url.hostname !== "127.0.0.1")
            ) {
                return null;
            }

            const urlPort = Number(
                url.port || (url.protocol === "https:" ? "443" : "80"),
            );
            if (url.username || url.password || urlPort !== port) return null;
            return { kind: "web", url: url.href };
        } catch {
            return null;
        }
    }

    if (mapping.applicationProtocol && mapping.applicationProtocol !== "raw")
        return null;

    const address = `127.0.0.1:${port}`;
    return {
        kind: "raw",
        address,
        sshUrl: `ssh://${address}`,
        sshCommand: `ssh -p ${port} 127.0.0.1`,
        rdpFile: supportsRdp ? `full address:s:${address}\r\n` : null,
    };
}
