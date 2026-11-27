import { expect, it, vi } from "vitest";
import type { ApiResult, EndpointOutcome } from "../../../src/protocol/types";
import { localNotice } from "../../../src/protocol/notices";
import { EndpointCache, privateDisplayLifetimeMs } from "../../../src/hooks/endpointCache";

it("revalidates one current screen's reads in sequence", async () => {
  const cache = new EndpointCache();
  const events: string[] = [];
  let release: (() => void) | undefined;
  cache.registerRefresh("/cases", async () => {
    events.push("first");
    await new Promise<void>((resolve) => {
      release = resolve;
    });
    events.push("settled");
  });
  cache.registerRefresh(
    "/cases",
    () => {
      events.push("second");
      return Promise.resolve();
    },
    "case-list",
  );
  cache.registerRefresh(
    "/cases",
    () => {
      events.push("duplicate");
      return Promise.resolve();
    },
    "case-list",
  );
  cache.registerRefresh("/people", () => {
    events.push("other");
    return Promise.resolve();
  });
  const first = cache.revalidate("/cases");
  await Promise.resolve();
  expect(events).toEqual(["first"]);
  expect(cache.revalidate("/cases")).toBe(first);
  release?.();
  await first;
  expect(events).toEqual(["first", "settled", "second"]);
  await cache.revalidate("/people");
  expect(events).toEqual(["first", "settled", "second", "other"]);
});

it("masks the old display and discards a reply held across reauthorization", async () => {
  const cache = new EndpointCache();
  const display = cache.display();
  const oldReply: ApiResult<EndpointOutcome> = {
    kind: "deliveryFailure",
    notice: localNotice("downloadFailed"),
  };
  const key = "held";
  const request = Promise.withResolvers<ApiResult<EndpointOutcome>>();
  const cached = cache.fetch(key, () => request.promise);
  cache.changeDisplay(false);
  expect(cache.admitsDisplay(display)).toBe(false);
  expect(cache.snapshot(key).previous).toBeNull();
  cache.changeDisplay(true);
  request.resolve(oldReply);
  expect(await cached).not.toEqual(oldReply);
  expect(cache.snapshot(key).result).toBeNull();
});

it("does not admit an expired snapshot when its timer has not run", async () => {
  let now = 0;
  const clock = vi.spyOn(performance, "now").mockImplementation(() => now);
  try {
    const cache = new EndpointCache();
    const reply: ApiResult<EndpointOutcome> = {
      kind: "deliveryFailure",
      notice: localNotice("downloadFailed"),
    };
    await cache.fetch("current", () => Promise.resolve(reply));
    expect(cache.snapshot("current").result).toEqual(reply);

    now = privateDisplayLifetimeMs;
    expect(cache.snapshot("current").result).toBeNull();
    expect(cache.snapshot("current").previous).toBeNull();
  } finally {
    clock.mockRestore();
  }
});
