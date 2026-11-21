import { randomUUID } from "node:crypto";
import { writeFile } from "node:fs/promises";
import { expect, test, type Page } from "./fixtures";
import { observeTrustedTypes, type TrustedTypesViolation } from "./trusted-types-observer";
import { startOpen } from "./case-workflow";
import { openAuthenticated } from "./session-helpers";
import { fillBusinessField } from "./typed-entry";

const editDate = async (page: Page): Promise<void> => {
  await openAuthenticated(page);
  await startOpen(page, `TT-DATE-${randomUUID()}`);
  await fillBusinessField(page, "incidentDate", "2026-09-01");

  const field = page.locator('[data-field-name="incidentDate"]');
  const canonical = field.locator('input[type="hidden"]');
  await expect(canonical).toHaveValue("2026-09-01");
  const day = field.locator('[data-type="day"]');
  await day.focus();
  for (const next of ["2026-09-02", "2026-09-03"]) {
    await day.press("ArrowUp");
    await expect(canonical).toHaveValue(next);
    await expect(day).toBeFocused();
  }
};

test("Chromium blocks a harmless untrusted HTML assignment @chromium", async ({
  browser,
  page,
  baseURL,
}, info) => {
  if (baseURL === undefined) {
    throw new Error("Expected the published workspace origin.");
  }
  const workspace = await page.request.get("/");
  expect(workspace.ok()).toBe(true);
  const policy = workspace.headers()["content-security-policy"];
  if (policy === undefined) {
    throw new Error("Expected the published workspace CSP.");
  }
  expect(policy).toContain("require-trusted-types-for 'script'");
  expect(policy).toContain("trusted-types 'none'");

  // Route only this localhost control document. Its origin is separate from the workspace.
  const address = new URL("/trusted-types-acceptance-control", baseURL);
  address.hostname = "127.0.0.1";
  const control = await browser.newContext({ ignoreHTTPSErrors: true });
  const events: TrustedTypesViolation[] = [];
  try {
    await observeTrustedTypes(control, address.origin, events, "__claimcoreTrustedTypesControl");
    await control.route(address.href, (route) =>
      route.fulfill({
        headers: { "content-security-policy": policy, "content-type": "text/html" },
        body: "<!doctype html><main></main>",
      }),
    );
    const controlPage = await control.newPage();
    await controlPage.goto(address.href);
    const blocked = await controlPage.locator("main").evaluate((element) => {
      try {
        element.innerHTML = "<b>Harmless policy control</b>";
        return false;
      } catch (error) {
        return error instanceof TypeError;
      }
    });
    expect(blocked).toBe(true);
    await expect.poll(() => events.length).toBe(1);
    expect(events[0]?.directive).toBe("require-trusted-types-for");
    await writeFile(info.outputPath("trusted-types-control.json"), JSON.stringify(events), {
      mode: 0o600,
    });
  } finally {
    await control.close();
  }
});

test("Chromium edits focused date segments under Trusted Types @chromium", async ({ page }) => {
  await editDate(page);
});
