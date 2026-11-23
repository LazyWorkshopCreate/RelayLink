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
        try {
            const authority = /^https?:\/\/([^/?#\\]+)(?:[/?#]|$)/i.exec(
                mapping.accessUrl,
            )?.[1];
            if (!authority || authority.includes("@")) return null;

            const url = new URL(mapping.accessUrl);
            if (url.protocol !== "http:" && url.protocol !== "https:")
                return null;

            const urlPort = Number(
                url.port || (url.protocol === "https:" ? "443" : "80"),
            );
            if (url.username || url.password || urlPort !== port) return null;
            return { kind: "web", url: mapping.accessUrl };
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
