import { EventEmitter } from "node:events";
import type { Page, Request, Response } from "@playwright/test";
import { afterEach, expect, it, vi } from "vitest";
import { observeAuthorityRefresh } from "../e2e/authority-read-evidence";
import { generatedWebValue, hostCases } from "./contract-corpus.fixtures";

afterEach(() => vi.restoreAllMocks());
const request = (endpoint: string, act?: object) =>
  ({
    url: () => `https://localhost:5443/api/${endpoint}`,
    postDataJSON: () => ({ act }),
  }) as Request;
const response = (source: Request, status: number, value: unknown) =>
  ({
    request: () => source,
    status: () => status,
    json: () => Promise.resolve(value),
  }) as Response;
const pageFixture = () => {
  const events = new EventEmitter();
  const absent = { count: () => Promise.resolve(0) };
  const picker = {
    count: () => Promise.resolve(1),
    locator: () => ({ count: () => Promise.resolve(3) }),
    isEnabled: () => Promise.resolve(true),
  };
  const page = Object.assign(events, {
    getByRole: () => absent,
    locator: () => ({ getByRole: () => absent, getByLabel: () => picker }),
  }) as unknown as Page;
  return { events, page };
};
const completeBodies = async () => {
  await Promise.resolve();
  await Promise.resolve();
};

it("reports only fresh validated sibling outcomes and a fixed UI phase on failure", async () => {
  const { events, page } = pageFixture();
  const log = vi.spyOn(console, "log").mockImplementation(() => undefined);
  const { finish } = observeAuthorityRefresh(page);
  const old = request("context");
  events.emit("request", old);
  events.emit("request", request("authority.execute", { kind: "SET_ENABLED", enabled: false }));
  const context = request("context");
  const listing = request("authority.list");
  events.emit("request", context);
  events.emit("request", listing);
  events.emit("response", response(context, 400, hostCases()[0]!.value));
  events.emit("response", response(listing, 200, generatedWebValue("authority.list")));
  events.emit("response", response(old, 200, generatedWebValue("context")));
  await completeBodies();
  await finish(true);
  expect(log.mock.calls.flatMap(([line]) => String(line).split("\n"))).toEqual([
    "E2E_AUTHORITY_PHASE USABLE_PICKER",
    "E2E_AUTHORITY_ADVICE UNOBSERVED 0",
    "E2E_AUTHORITY_PICKER UNOBSERVED UNOBSERVED",
    "E2E_AUTHORITY_READ CONTEXT 400 HOST_FAILURE HOST_INPUTINVALID",
    "E2E_AUTHORITY_READ LIST 200 SUCCEEDED NONE",
  ]);
  expect(events.listenerCount("response")).toBe(0);
});

it("keeps invalid and transport states separate without printing private bodies", async () => {
  const { events, page } = pageFixture();
  const log = vi.spyOn(console, "log").mockImplementation(() => undefined);
  const { finish } = observeAuthorityRefresh(page);
  events.emit("request", request("authority.execute", { kind: "SET_ENABLED", enabled: false }));
  const context = request("context");
  const listing = request("authority.list");
  events.emit("request", context);
  events.emit("request", listing);
  events.emit("response", response(context, 503, { message: "private-token claimant-body" }));
  events.emit("requestfailed", listing);
  await completeBodies();
  await finish(true);
  expect(log.mock.calls.flatMap(([line]) => String(line).split("\n"))).toEqual([
    "E2E_AUTHORITY_PHASE USABLE_PICKER",
    "E2E_AUTHORITY_ADVICE UNOBSERVED 0",
    "E2E_AUTHORITY_PICKER UNOBSERVED UNOBSERVED",
    "E2E_AUTHORITY_READ CONTEXT 503 INVALID NONE",
    "E2E_AUTHORITY_READ LIST 0 TRANSPORT NONE",
  ]);
});

it("does not emit observations on success or wait for unfinished response bodies", async () => {
  const { events, page } = pageFixture();
  const log = vi.spyOn(console, "log").mockImplementation(() => undefined);
  const { finish } = observeAuthorityRefresh(page);
  events.emit("request", request("authority.execute", { kind: "SET_ENABLED", enabled: false }));
  const context = request("context");
  events.emit("request", context);
  const body = Promise.withResolvers<unknown>();
  events.emit("response", {
    request: () => context,
    status: () => 200,
    json: () => body.promise,
  });
  await finish(false);
  body.resolve(generatedWebValue("context"));
  await completeBodies();
  expect(log).not.toHaveBeenCalled();
  expect(events.eventNames()).toEqual([]);
});

it("retains pre-teardown phase, declared budget and validated grant advice", async () => {
  const clock = vi.spyOn(performance, "now").mockReturnValue(0);
  const { events, page } = pageFixture();
  const log = vi.spyOn(console, "log").mockImplementation(() => undefined);
  const evidence = observeAuthorityRefresh(page, { timeout: 30_000 });
  events.emit("request", request("authority.execute", { kind: "SET_ENABLED", enabled: false }));
  const context = request("context");
  const value = generatedWebValue("context");
  if (value.outcome.tag !== "SUCCEEDED") {
    throw new Error("Missing context fixture");
  }
  const accepted = {
    ...value,
    outcome: {
      ...value.outcome,
      data: {
        ...value.outcome.data,
        offers: [
          { definition: "SET_GRANT", caseReference: null, expectedRevision: null, choices: [] },
        ],
      },
    },
  };
  events.emit("request", context);
  events.emit("response", response(context, 200, accepted));
  await completeBodies();
  clock.mockReturnValue(32_000);
  await evidence.stage("NEXT_GRANT");
  vi.spyOn(page, "locator").mockImplementation(() => {
    throw new Error("Closed private page");
  });
  clock.mockReturnValue(33_000);
  await evidence.finish(true);
  expect(log.mock.calls.flatMap(([line]) => String(line).split("\n"))).toEqual([
    "E2E_AUTHORITY_PHASE USABLE_PICKER",
    "E2E_AUTHORITY_ADVICE GRANT 1",
    "E2E_AUTHORITY_PICKER ENABLED GRANT",
    "E2E_AUTHORITY_BODY_TIMING NEXT_GRANT 32000 30000",
    "E2E_AUTHORITY_BODY_TIMING FAILURE 33000 30000",
    "E2E_AUTHORITY_READ CONTEXT 200 SUCCEEDED NONE",
    "E2E_AUTHORITY_READ LIST 0 UNOBSERVED NONE",
  ]);
});
