import type * as Navigation from "../src/routing/navigation";
import { readFile } from "node:fs/promises";
import { transformWithOxc } from "vite";
import { expect, test } from "@playwright/test";

// Serve the actual routing owner through the same TS transform used by the frontend build.
// Only the document/asset delivery is a fixture; History traversal is the pinned native engine.
test("@engine cancels an approved in-flight owner traversal and preserves unknown entries", async ({
  page,
}) => {
  const source = await readFile(new URL("../src/routing/navigation.ts", import.meta.url), "utf8");
  const module = await transformWithOxc(source, "navigation.ts");
  await page.route("**/navigation-check/**", async (route) => {
    await route.fulfill({
      contentType: route.request().url().endsWith("navigation.js")
        ? "text/javascript"
        : "text/html",
      body: route.request().url().endsWith("navigation.js")
        ? module.code
        : "<!doctype html><title>History control</title>",
    });
  });
  await page.goto("/navigation-check/first");
  const observed = await page.evaluate(async () => {
    const modulePath = "/navigation-check/navigation.js";
    const nav = (await import(modulePath)) as typeof Navigation;
    const until = async (predicate: () => boolean) => {
      const deadline = Date.now() + 3000;
      while (!predicate()) {
        if (Date.now() > deadline) {
          throw new Error("History transition did not settle");
        }
        await new Promise((resolve) => {
          setTimeout(resolve, 5);
        });
      }
    };
    nav.setRouteFocusOwner("first-session");
    nav.navigate("/navigation-check/first", true);
    nav.navigate("/navigation-check/middle");
    nav.navigate("/navigation-check/draft");
    const { length } = history;
    let approve: (() => void) | undefined;
    const release = nav.setNavigationBlocker((proceed) => {
      approve = proceed;
      return false;
    });
    history.go(-2);
    await until(() => approve !== undefined && location.pathname === "/navigation-check/draft");
    let pops = 0;
    const count = () => {
      pops += 1;
    };
    window.addEventListener("popstate", count);
    const published: string[] = [];
    const unsubscribe = nav.subscribeRoute(() => {
      published.push(nav.routeSnapshot());
    });
    approve!();
    nav.setRouteFocusOwner("replacement-session");
    release();
    await until(() => pops >= 2);
    const cancelled = {
      path: location.pathname,
      snapshot: nav.routeSnapshot(),
      published: [...published],
      length: history.length,
    };
    history.back();
    await until(() => nav.routeSnapshot() === "/navigation-check/middle");
    history.forward();
    await until(() => nav.routeSnapshot() === "/navigation-check/draft");
    history.pushState(null, "", "/navigation-check/unknown-null");
    history.pushState({ unrelated: "retained" }, "", "/navigation-check/unknown-object");
    history.back();
    await until(() => nav.routeSnapshot() === "/navigation-check/unknown-null");
    const unknownLength = history.length;
    history.forward();
    await until(() => nav.routeSnapshot() === "/navigation-check/unknown-object");
    unsubscribe();
    window.removeEventListener("popstate", count);
    return {
      cancelled,
      length,
      unknown: {
        state: history.state as unknown,
        length: history.length,
        expectedLength: unknownLength,
      },
    };
  });
  expect(observed.cancelled).toEqual({
    path: "/navigation-check/draft",
    snapshot: "/navigation-check/draft",
    published: [],
    length: observed.length,
  });
  expect(observed.unknown.state).toEqual({ unrelated: "retained" });
  expect(observed.unknown.length).toBe(observed.unknown.expectedLength);
});
