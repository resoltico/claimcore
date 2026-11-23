import { expect, it } from "vitest";
import { EndpointCache } from "../../../src/hooks/endpointCache";

it("revalidates one current screen's reads in sequence and throttles automatic retries", async () => {
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
  cache.registerRefresh("/cases", () => {
    events.push("second");
    return Promise.resolve();
  });
  cache.registerRefresh("/people", () => {
    events.push("other");
    return Promise.resolve();
  });
  const first = cache.revalidate("/cases", 0);
  await Promise.resolve();
  expect(events).toEqual(["first"]);
  expect(cache.revalidate("/cases", 1)).toBe(first);
  release?.();
  await first;
  expect(events).toEqual(["first", "settled", "second"]);
  await cache.revalidate("/cases", 29_999);
  expect(events).toHaveLength(3);
  await cache.revalidate("/people", 30_000);
  expect(events).toEqual(["first", "settled", "second", "other"]);
});
