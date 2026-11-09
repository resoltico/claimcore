import { afterEach, expect, it, vi } from "vitest";
import { requestEndpoint } from "../../../src/api/service";
import { EndpointCache, endpointCacheKey } from "../../../src/hooks/endpointCache";

const human = {
  representation: "HUMAN",
  outcome: {
    kind: "REJECTED",
    rejection: {
      kind: "REFUSED",
      advice: {
        action: "ASK_FOR_ACCESS",
        message: { messageId: "diagnostic.RESOURCE_UNAVAILABLE", arguments: [] },
      },
    },
  },
};
const data = {
  representation: "DATA",
  outcome: {
    kind: "REJECTED",
    data: { code: "RESOURCE_UNAVAILABLE", action: "ASK_FOR_ACCESS" },
  },
};
const input = { target: { kind: "REFERENCE", caseReference: "SYNTHETIC-CASE" } } as const;
const response = (value: unknown) =>
  new Response(JSON.stringify(value), {
    status: 200,
    headers: { "Content-Type": "application/json" },
  });
afterEach(() => vi.unstubAllGlobals());

it("sends transport selection explicitly and rejects a structurally valid reply of the other representation", async () => {
  const fetch = vi.fn<typeof globalThis.fetch>().mockResolvedValueOnce(response(data));
  vi.stubGlobal("fetch", fetch);
  const result = await requestEndpoint("case.get", "csrf", input, undefined, undefined, "HUMAN");
  expect(result.kind).toBe("deliveryFailure");
  expect(fetch).toHaveBeenCalledOnce();
  expect(fetch.mock.calls[0]?.[1]?.headers).toMatchObject({ "ClaimCore-Representation": "HUMAN" });
  expect(fetch.mock.calls[0]?.[1]?.body).toBe(JSON.stringify(input));
});

it("keeps concurrent HUMAN and DATA observations distinct through the actual request cache", async () => {
  const fetch = vi.fn<typeof globalThis.fetch>().mockImplementation((_url, options) => {
    const headers = new Headers(options?.headers);
    return Promise.resolve(
      response(headers.get("ClaimCore-Representation") === "HUMAN" ? human : data),
    );
  });
  vi.stubGlobal("fetch", fetch);
  const cache = new EndpointCache();
  const humanKey = endpointCacheKey("owner-installation-actor-epoch", "case.get", input, "HUMAN");
  const dataKey = endpointCacheKey("owner-installation-actor-epoch", "case.get", input, "DATA");
  const observe = (representation: "HUMAN" | "DATA", key: string) =>
    cache.fetch(key, (signal) =>
      requestEndpoint("case.get", "csrf", input, signal, undefined, representation),
    );
  const first = observe("HUMAN", humanKey);
  const same = observe("HUMAN", humanKey);
  const separate = observe("DATA", dataKey);
  expect(first).toBe(same);
  expect(first).not.toBe(separate);
  const [humanResult, dataResult] = await Promise.all([first, separate]);
  expect(humanResult.kind).toBe("outcome");
  expect(dataResult.kind).toBe("outcome");
  if (humanResult.kind !== "outcome" || dataResult.kind !== "outcome") {
    throw new Error("Expected both separately admitted observations.");
  }
  expect(humanResult.value).toEqual(human);
  expect(dataResult.value).toEqual(data);
  expect(fetch).toHaveBeenCalledTimes(2);
  expect(fetch.mock.calls.map((call) => call[1]?.body)).toEqual([
    JSON.stringify(input),
    JSON.stringify(input),
  ]);
});
