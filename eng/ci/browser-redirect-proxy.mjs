// Controlled synthetic fixture: preserve the public HTTPS origin and every native response
// policy. Only armed test requests replace a completed native response with a redirect.
import { readFileSync, writeFileSync } from "node:fs";
import https from "node:https";
import { join } from "node:path";

const [state, originText, upstreamText] = process.argv.slice(2);
if (!state || !originText || !upstreamText) {
  process.exit(64);
}
const origin = new URL(originText);
const upstreamPort = Number(upstreamText);
if (
  origin.protocol !== "https:" ||
  origin.hostname !== "localhost" ||
  !Number.isInteger(upstreamPort)
) {
  process.exit(64);
}
const pfx = readFileSync(join(state, "web.pfx"));
const ca = readFileSync(join(state, "web-ca.pem"));
const controlFile = join(state, "redirect-control.json");
const evidenceFile = join(state, "redirect-evidence.json");
/** @typedef {{id:string,path:string,status:number,originals:number,destinations:number,completed:boolean,reply?:unknown}} RedirectControl */
/** @type {RedirectControl | undefined} */
let armed;
const allowed = new Set([
  "/api/v3/operations/submit",
  "/api/v3/recovery/export",
  "/__synthetic/redirect-control",
]);

/** @param {unknown} value */
function parsedControl(value) {
  if (
    typeof value !== "object" ||
    value === null ||
    !("path" in value) ||
    typeof value.path !== "string" ||
    !("status" in value) ||
    typeof value.status !== "number" ||
    !("id" in value) ||
    typeof value.id !== "string"
  ) {
    return undefined;
  }
  return { path: value.path, status: value.status, id: value.id };
}
function control() {
  try {
    /** @type {unknown} */
    const value = JSON.parse(readFileSync(controlFile, "utf8"));
    const next = parsedControl(value);
    if (
      next &&
      allowed.has(next.path) &&
      [301, 302, 303, 307, 308].includes(next.status) &&
      next.id !== armed?.id
    ) {
      armed = { ...next, originals: 0, destinations: 0, completed: false };
    }
  } catch {
    /* No armed fault is the normal fixture state. */
  }
}
/** @param {RedirectControl} current */
function record(current) {
  writeFileSync(evidenceFile, JSON.stringify(current), { mode: 0o600 });
}
/** @param {import("node:http").ServerResponse} response @param {import("node:http").OutgoingHttpHeaders} headers @param {RedirectControl} current */
function redirect(response, headers, current) {
  const output = { ...headers, location: `${origin.origin}/__synthetic/redirect-destination` };
  delete output["content-length"];
  delete output["content-encoding"];
  response.writeHead(current.status, output);
  response.end();
  record(current);
}
/** @param {import("node:http").ServerResponse} response @param {import("node:http").IncomingMessage} reply @param {RedirectControl} current */
function completed(response, reply, current) {
  /** @type {Buffer[]} */
  const chunks = [];
  let bytes = 0;
  reply.on("data", (/** @type {Buffer} */ chunk) => {
    bytes += chunk.length;
    if (bytes > 2097152) {
      reply.destroy();
    } else {
      chunks.push(chunk);
    }
  });
  reply.on("end", () => {
    current.completed = true;
    if (current.path === "/api/v3/operations/submit") {
      try {
        current.reply = JSON.parse(Buffer.concat(chunks).toString("utf8"));
      } catch {
        current.reply = null;
      }
    }
    redirect(response, reply.headers, current);
  });
  reply.on("error", () => response.destroy());
}
/** @param {import("node:http").IncomingMessage} request @param {import("node:http").ServerResponse} response */
function handle(request, response) {
  control();
  const current = armed;
  if (current && request.url === "/__synthetic/redirect-destination") {
    current.destinations += 1;
    record(current);
    request.resume();
    response.writeHead(200, { "content-type": "application/json" });
    response.end("{}");
    return;
  }
  const fault = current && request.url === current.path && current.originals === 0;
  if (fault) {
    current.originals += 1;
    if (current.path === "/__synthetic/redirect-control") {
      request.resume();
      redirect(response, {}, current);
      return;
    }
  }
  forward(request, response, fault ? current : undefined);
}
/** @param {import("node:http").IncomingMessage} request @param {import("node:http").ServerResponse} response @param {RedirectControl | undefined} current */
function forward(request, response, current) {
  const upstream = https.request(
    {
      hostname: "localhost",
      port: upstreamPort,
      ca,
      method: request.method,
      path: request.url,
      headers: request.headers,
    },
    (reply) => {
      if (current) {
        completed(response, reply, current);
      } else {
        response.writeHead(reply.statusCode ?? 502, reply.headers);
        reply.pipe(response);
      }
    },
  );
  upstream.on("error", () => {
    if (!response.headersSent) {
      response.writeHead(502);
    }
    response.end();
  });
  request.on("aborted", () => upstream.destroy());
  response.on("close", () => {
    if (!response.writableFinished) {
      upstream.destroy();
    }
  });
  request.pipe(upstream);
}
const server = https.createServer({ pfx }, handle);
pfx.fill(0);
server.headersTimeout = 15000;
server.requestTimeout = 30000;
server.on("error", () => {
  process.stderr.write("Synthetic redirect proxy failed.\n");
  process.exitCode = 1;
});
server.listen(Number(origin.port), "127.0.0.1");
process.on("SIGTERM", () => {
  server.closeAllConnections();
  server.close();
});
