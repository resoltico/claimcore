import { expect, test } from "@playwright/test";
import { createServer } from "node:http";
import type { AddressInfo } from "node:net";
import { readFileSync } from "node:fs";
import { stripTypeScriptTypes } from "node:module";
import type * as Scheduler from "../src/recovery/recoveryLookup";

test("ready recovery work passes another identity's backoff and the next tab takes over @engine", async ({
  browser,
}) => {
  const scheduler = stripTypeScriptTypes(
    readFileSync(new URL("../src/recovery/recoveryLookup.ts", import.meta.url), "utf8"),
  );
  const calls: string[] = [];
  const slowLookup = Promise.withResolvers<void>();
  const finishSlowLookup = Promise.withResolvers<void>();
  let slowCount = 0;
  const host = createServer((request, response) => {
    const url = new URL(request.url ?? "/", "http://127.0.0.1");
    if (url.pathname === "/recovery/scheduler.js" || url.pathname === "/api/service") {
      response.setHeader("content-type", "text/javascript");
      response.end(
        url.pathname === "/api/service"
          ? 'export const service = {operationGet: async (id) => (await fetch("/lookup?id=" + id)).json()};'
          : scheduler,
      );
      return;
    }
    if (url.pathname === "/lookup") {
      const id = url.searchParams.get("id") ?? "";
      calls.push(id);
      response.setHeader("content-type", "application/json");
      const reply = JSON.stringify(
        calls.length === 1
          ? {
              kind: "hostFailure",
              status: 429,
              failure: { code: "RATE_LIMITED" },
              retryAfterMs: 3500,
            }
          : { kind: "outcome", value: { outcome: { kind: "NOT_ACCEPTED" } } },
      );
      if (id === "C" && ++slowCount === 1) {
        slowLookup.resolve();
        void finishSlowLookup.promise.then(() => {
          response.end(reply);
        });
      } else {
        response.end(reply);
      }
      return;
    }
    response.setHeader("content-type", "text/html");
    response.end("<title>Synthetic account recovery</title>");
  });
  await new Promise<void>((resolve) => {
    host.listen(0, "127.0.0.1", resolve);
  });

  const context = await browser.newContext();
  try {
    const first = await context.newPage();
    const second = await context.newPage();
    const origin = `http://127.0.0.1:${(host.address() as AddressInfo).port}`;
    await Promise.all([first.goto(origin), second.goto(origin)]);
    const own = (page: typeof first) =>
      page.evaluate(async () => {
        const path = "/recovery/scheduler.js";
        const { leadRecovery } = (await import(path)) as typeof Scheduler;
        leadRecovery(
          { serviceOrigin: location.origin, installationId: "fixture", actorId: "fixture" },
          new AbortController().signal,
          (leadership) => {
            document.documentElement.dataset["leader"] = String(leadership !== null);
          },
        );
      });
    const lookup = (page: typeof first, operationId: string, duplicate = false) =>
      page.evaluate(
        async ([id, join]) => {
          const path = "/recovery/scheduler.js";
          const { recoveryLookup } = (await import(path)) as typeof Scheduler;
          const record = {
            serviceOrigin: location.origin,
            installationId: "fixture",
            actorId: "fixture",
            operationId: id,
            attemptId: id,
            format: 2,
            subject: "CASE",
            act: "case.close",
          } as const;
          const run = () => recoveryLookup(record, "fixture", 0, new AbortController().signal);
          return join ? Promise.all([run(), run()]) : run();
        },
        [operationId, duplicate] as const,
      );

    await first.bringToFront();
    await own(first);
    await expect(first.locator("html")).toHaveAttribute("data-leader", "true");
    await own(second);
    await expect(second.locator("html")).not.toHaveAttribute("data-leader", "true");
    expect(await lookup(first, "A")).toMatchObject({ status: 429 });
    const retry = lookup(first, "A").catch(() => null);
    expect(await lookup(second, "B")).toMatchObject({ kind: "outcome" });
    expect(calls).toEqual(["A", "B"]);

    await first.close();
    await second.bringToFront();
    await retry;
    await expect(second.locator("html")).toHaveAttribute("data-leader", "true");
    expect(await lookup(second, "A", true)).toHaveLength(2);
    expect(calls).toEqual(["A", "B", "A"]);

    const peer = await context.newPage();
    await peer.goto(origin);
    // Both tabs can read an identity using their own session. A healthy slow
    // read renews its permit beyond the initial expiry, so they never overlap.
    const slow = lookup(second, "C");
    await slowLookup.promise;
    const queued = lookup(peer, "C");
    await new Promise<void>((resolve) => {
      setTimeout(resolve, 3400);
    });
    expect(slowCount).toBe(1);
    finishSlowLookup.resolve();
    await Promise.all([slow, queued]);
    expect(slowCount).toBe(2);
  } finally {
    finishSlowLookup.resolve();
    await context.close();
    await new Promise<void>((resolve, reject) => {
      host.close((error) => {
        if (error === undefined) {
          resolve();
        } else {
          reject(error);
        }
      });
    });
  }
});
