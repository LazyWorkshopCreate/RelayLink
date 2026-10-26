import http from "node:http";
import { readFile, stat } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";

const directory = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../artifacts/site");
const base = "/RelayLink/";
const types = { ".html": "text/html; charset=utf-8", ".css": "text/css; charset=utf-8", ".js": "text/javascript; charset=utf-8", ".svg": "image/svg+xml", ".png": "image/png" };
http.createServer(async (request, response) => {
    try {
        const url = new URL(request.url, "http://localhost");
        if (!url.pathname.startsWith(base)) { response.writeHead(404).end(); return; }
        let file = path.resolve(directory, decodeURIComponent(url.pathname.slice(base.length)));
        const resolved = path.relative(directory, file);
        if (resolved.startsWith("..") || path.isAbsolute(resolved)) { response.writeHead(404).end(); return; }
        if ((await stat(file)).isDirectory()) {
            if (!url.pathname.endsWith("/")) { response.writeHead(301, { Location: url.pathname + "/" + url.search }).end(); return; }
            file = path.join(file, "index.html");
        }
        response.writeHead(200, { "Content-Type": types[path.extname(file)] ?? "application/octet-stream" });
        response.end(await readFile(file));
    } catch { response.writeHead(404).end("Not found"); }
}).listen(4178, "127.0.0.1", () => console.log("Preview: http://127.0.0.1:4178/RelayLink/ (Ctrl+C to stop)"));
