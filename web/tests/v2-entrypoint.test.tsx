import { afterEach, expect, it, vi } from "vitest";

afterEach(() => {
  document.body.replaceChildren();
  vi.resetModules();
});

it("mounts the Web v2 browser root", async () => {
  document.body.innerHTML = '<div id="root"></div>';
  vi.stubGlobal(
    "fetch",
    vi.fn().mockResolvedValue(
      new Response(
        JSON.stringify({
          endpoint: "session",
          outcome: { tag: "SNAPSHOT", data: { authenticated: false, antiforgeryToken: "token" } },
        }),
        { headers: { "content-type": "application/json" } },
      ),
    ),
  );
  await import("../src/main");
  await new Promise((resolve) => setTimeout(resolve, 0));
  expect(document.querySelector("#root")?.innerHTML).not.toBe("");
});

it("fails immediately when the embedding document omits the application root", async () => {
  await expect(import("../src/main")).rejects.toThrow("Missing ClaimCore root element.");
});
