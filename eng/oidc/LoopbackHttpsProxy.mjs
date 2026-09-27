import { readFileSync, writeFileSync } from "node:fs";
import http from "node:http";
import https from "node:https";

const [certificatePath, keyPath, upstreamPortFile, listenerPortFile] = process.argv.slice(2);
if (![certificatePath, keyPath, upstreamPortFile, listenerPortFile].every(Boolean)) process.exit(64);

const server = https.createServer(
  { cert: readFileSync(certificatePath), key: readFileSync(keyPath) },
  (request, response) => {
    let upstreamPort;
    try {
      upstreamPort = Number(readFileSync(upstreamPortFile, "utf8"));
      if (!Number.isInteger(upstreamPort) || upstreamPort < 1 || upstreamPort > 65535)
        throw new Error("upstream unavailable");
    } catch {
      response.writeHead(503);
      response.end();
      return;
    }

    const externalPort = request.socket.localPort;
    const headers = {
      ...request.headers,
      host: `127.0.0.1:${upstreamPort}`,
      "x-forwarded-proto": "https",
      "x-forwarded-host": `127.0.0.1:${externalPort}`,
      "x-forwarded-port": String(externalPort),
    };
    delete headers.forwarded;
    delete headers["x-forwarded-for"];

    const upstream = http.request(
      {
        hostname: "127.0.0.1",
        port: upstreamPort,
        method: request.method,
        path: request.url,
        headers,
      },
      (result) => {
        response.writeHead(result.statusCode ?? 502, result.headers);
        result.pipe(response);
      },
    );
    upstream.on("error", () => {
      if (!response.headersSent) response.writeHead(502);
      response.end();
    });
    request.pipe(upstream);
  },
);

server.headersTimeout = 15_000;
server.requestTimeout = 30_000;
server.listen(0, "127.0.0.1", () => {
  const address = server.address();
  if (typeof address === "object" && address !== null)
    writeFileSync(listenerPortFile, String(address.port), { mode: 0o600, flag: "wx" });
});

process.on("SIGTERM", () => server.close());
