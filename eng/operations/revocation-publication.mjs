import { readFileSync, statSync } from "node:fs";
import { createServer } from "node:http";

const path = "/publication/ca.crl";
const metadata = statSync(path);
if (!metadata.isFile() || metadata.size === 0 || metadata.size > 1024 * 1024) {
  throw new Error("Signed local revocation publication is unavailable.");
}
const bytes = readFileSync(path);
const server = createServer((request, response) => {
  if (request.method !== "GET" || request.url !== "/ca.crl") {
    response.writeHead(404).end();
    return;
  }
  process.stdout.write(`${JSON.stringify({ crlRequest: request.socket.remoteAddress })}\n`);
  response
    .writeHead(200, {
      "Content-Type": "application/pkix-crl",
      "Content-Length": bytes.length,
      "Cache-Control": "no-store",
    })
    .end(bytes);
});
server.listen(8000, "0.0.0.0");
const stop = () => server.close();
process.on("SIGTERM", stop);
process.on("SIGINT", stop);
