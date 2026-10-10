import { randomUUID } from "node:crypto";
import { createServer, type ServerResponse } from "node:http";
import { gzipSync } from "node:zlib";
import { expect, test } from "@playwright/test";
import { retainResponseBodies } from "./response-bodies";

const raw = Buffer.from(JSON.stringify({ synthetic: true, padding: "x".repeat(262144) }));

const streamReply = (response: ServerResponse, bytes: Buffer): void => {
  response.setHeader("Content-Length", bytes.length);
  response.flushHeaders();
  let offset = 0;
  const timer = setInterval(() => {
    const end = Math.min(bytes.length, offset + Math.ceil(bytes.length / 8));
    response.write(bytes.subarray(offset, end));
    offset = end;
    if (offset === bytes.length) {
      clearInterval(timer);
      response.end();
    }
  }, 5);
  response.once("close", () => {
    clearInterval(timer);
  });
};

const startServer = async (compressed: boolean) => {
  const identity = randomUUID();
  let requests = 0;
  const server = createServer((request, response) => {
    if (request.url !== "/probe") {
      response.end("<!doctype html><title>Synthetic transport control</title>");
      return;
    }
    requests += 1;
    if (request.method !== "POST") {
      response.writeHead(405).end();
      return;
    }
    response.setHeader("Content-Type", "application/json");
    response.setHeader("X-Synthetic-Reply", identity);
    if (compressed) {
      response.setHeader("Content-Encoding", "gzip");
    }
    streamReply(response, compressed ? gzipSync(raw) : raw);
  });
  const close = async () => {
    server.closeAllConnections();
    await new Promise<void>((resolve) => {
      server.close(() => {
        resolve();
      });
    });
  };
  await new Promise<void>((resolve, reject) => {
    server.once("error", reject);
    server.listen(0, "127.0.0.1", resolve);
  });
  const address = server.address();
  if (address === null || typeof address === "string") {
    await close();
    throw new Error("E2E_TRANSPORT_CONTROL_ADDRESS_UNAVAILABLE");
  }
  return { origin: `http://127.0.0.1:${address.port}`, identity, requests: () => requests, close };
};

for (const compressed of [false, true]) {
  test(`original ${compressed ? "gzip" : "raw"} streamed reply remains observable at headers`, async ({
    browser,
  }) => {
    const fixture = await startServer(compressed);
    try {
      const context = await browser.newContext({ storageState: { cookies: [], origins: [] } });
      try {
        const page = await context.newPage();
        await retainResponseBodies(page);
        await page.goto(fixture.origin);
        const observed = page.waitForResponse((reply) => reply.url() === `${fixture.origin}/probe`);
        const consumed = page.evaluate(async (url) => {
          const response = await fetch(`${url}/probe`, { method: "POST", credentials: "omit" });
          if (response.body === null) {
            throw new Error("E2E_TRANSPORT_CONTROL_BODY_UNAVAILABLE");
          }
          let count = 0;
          await response.body.pipeTo(
            new WritableStream<Uint8Array>({
              write(chunk) {
                count += chunk.byteLength;
                // Exercise native body observation while the consumer applies backpressure.
                return new Promise<void>((resolve) => {
                  setTimeout(resolve, 2);
                });
              },
            }),
          );
          return count;
        }, fixture.origin);
        const body = observed.then(async (reply) => {
          expect(reply.status()).toBe(200);
          expect(reply.request().method()).toBe("POST");
          expect(await reply.headerValue("X-Synthetic-Reply")).toBe(fixture.identity);
          return reply.body();
        });
        const [count, bytes] = await Promise.all([consumed, body]);
        expect(count).toBe(raw.length);
        expect(bytes.equals(raw)).toBe(true);
        expect(fixture.requests()).toBe(1);
      } finally {
        await context.close();
      }
    } finally {
      await fixture.close();
    }
  });
}
