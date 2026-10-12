import { readFile } from "node:fs/promises";
import { createServer, type Server } from "node:https";
import { test as base } from "@playwright/test";

type RedirectResponse = Readonly<{
  url: (status: number) => string;
  requests: () => number;
}>;

const listen = async (server: Server): Promise<number> => {
  await new Promise<void>((resolve, reject) => {
    server.once("error", reject);
    server.listen(0, "127.0.0.1", resolve);
  });
  const address = server.address();
  if (address === null || typeof address === "string") {
    throw new Error("E2E_REDIRECT_LISTENER_UNAVAILABLE");
  }
  return address.port;
};

// WebKit refuses Playwright redirect fulfillment. An actual TLS response exercises each
// engine's network redirect policy; the fault listener owns no business or authority port.
export const test = base.extend<{ redirectResponse: RedirectResponse }>({
  redirectResponse: async ({ baseURL }, provide) => {
    const certificate = process.env["CLAIMCORE_WEB_E2E_TLS_CERTIFICATE_FILE"];
    if (certificate === undefined || baseURL === undefined) {
      throw new Error("E2E_REDIRECT_FIXTURE_CONFIGURATION_MISSING");
    }
    const { origin } = new URL(baseURL);
    let requests = 0;
    const pfx = await readFile(certificate);
    const server = createServer({ pfx }, (request, response) => {
      request.resume();
      const status = Number(request.url?.slice(1));
      if ([301, 302, 303, 307, 308].includes(status)) {
        requests += 1;
        response.writeHead(status, {
          Location: `${origin}/__synthetic/redirect-destination`,
          "Access-Control-Allow-Origin": origin,
          "Access-Control-Allow-Credentials": "true",
        });
      } else {
        response.writeHead(400);
      }
      response.end();
    });
    pfx.fill(0);
    try {
      const port = await listen(server);
      await provide({
        url: (status) => `https://localhost:${port}/${status}`,
        requests: () => requests,
      });
    } finally {
      server.closeAllConnections();
      await new Promise<void>((resolve) => {
        server.close(() => {
          resolve();
        });
      });
    }
  },
});
