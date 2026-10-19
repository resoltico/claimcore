// @vitest-environment node
import { EventEmitter } from "node:events";
import type { Page } from "@playwright/test";
import { expect, it } from "vitest";
import { observeOidcSubmission } from "../e2e/oidc-provider-evidence.mjs";

const origin = "https://identity.example.test";
const credentials = { username: "PRIVATE_USER", password: "PRIVATE +& é" };
const encoded = "username=PRIVATE_USER&password=PRIVATE+%2B%26+%C3%A9&session=PRIVATE_SESSION";
const fixture = () => {
  const events = new EventEmitter();
  const page = {
    on: events.on.bind(events),
    mainFrame: () => "main",
  } as unknown as Page;
  return { events, evidence: observeOidcSubmission(page, origin, credentials) };
};
const form = (
  address = `${origin}/realms/synthetic/login-actions/authenticate?session=PRIVATE`,
  body = encoded,
  frame = "main",
  method = "POST",
  navigating = true,
) => ({
  url: () => address,
  postData: () => body,
  frame: () => frame,
  method: () => method,
  isNavigationRequest: () => navigating,
});
const reply = (request: ReturnType<typeof form>, status: number) => ({
  request: () => request,
  status: () => status,
});

it("distinguishes no submission from pending POST and emits no private data", () => {
  const { events, evidence } = fixture();
  expect(evidence()).toEqual({ status: "none", credentialMatch: false });
  events.emit("request", form());
  const facts = evidence();
  expect(facts).toEqual({ status: "pending", credentialMatch: true });
  expect(JSON.stringify(facts)).not.toContain("PRIVATE");
});

it("binds the response status to the exact selected submission and freezes earlier snapshots", () => {
  const { events, evidence } = fixture();
  const submitted = form();
  events.emit("request", submitted);
  const pending = evidence();
  events.emit("response", reply(form(), 401));
  expect(evidence().status).toBe("pending");
  events.emit("response", reply(submitted, 200));
  expect(evidence()).toEqual({ status: 200, credentialMatch: true });
  expect(pending.status).toBe("pending");
});

it.each([
  "username=PRIVATE_USER&password=wrong",
  "username=wrong&password=PRIVATE+%2B%26+%C3%A9",
  "username=PRIVATE_USER&username=PRIVATE_USER&password=PRIVATE+%2B%26+%C3%A9",
  "username=PRIVATE_USER&password=PRIVATE+%2B%26+%C3%A9&password=PRIVATE+%2B%26+%C3%A9",
])("does not match different or duplicate credentials: case %#", (body) => {
  const { events, evidence } = fixture();
  const submitted = form(undefined, body);
  events.emit("request", submitted);
  events.emit("response", reply(submitted, 401));
  const facts = evidence();
  expect(facts).toEqual({ status: 401, credentialMatch: false });
  expect(JSON.stringify(facts)).not.toContain("PRIVATE");
});

it.each([
  form("https://foreign.example.test/login-actions/authenticate"),
  form(`${origin}/unrelated`),
  form(undefined, undefined, "child"),
  form(undefined, undefined, undefined, "GET"),
  form(undefined, undefined, undefined, undefined, false),
])("ignores unrelated origins, paths, frames and methods: case %#", (unrelated) => {
  const { events, evidence } = fixture();
  events.emit("request", unrelated);
  events.emit("response", reply(unrelated, 500));
  expect(evidence()).toEqual({ status: "none", credentialMatch: false });
});

it.each([0, 600, Number.NaN])(
  "refuses invalid status values without exporting them: case %#",
  (status) => {
    const { events, evidence } = fixture();
    const submitted = form();
    events.emit("request", submitted);
    events.emit("response", reply(submitted, status));
    expect(evidence()).toEqual({ status: "other", credentialMatch: true });
  },
);

it("distinguishes the selected POST transport failure from a pending response", () => {
  const { events, evidence } = fixture();
  const submitted = form();
  events.emit("request", submitted);
  events.emit("requestfailed", form());
  expect(evidence().status).toBe("pending");
  events.emit("requestfailed", submitted);
  expect(evidence()).toEqual({ status: "failed", credentialMatch: true });
});
