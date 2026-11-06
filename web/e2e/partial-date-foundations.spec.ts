import { expect, test, type Locator } from "@playwright/test";
import { observe, seed } from "./presentation-probe";
import { expectAccessible } from "./session-helpers";
import { actorContextInput } from "../tests/support/inputs";
import { isResponse } from "../src/protocol/client";
const setPart = async (field: Locator, type: string, value: string) => {
  const segment = field.locator(`[role="spinbutton"][data-type="${type}"]`);
  await segment.focus();
  await segment.pressSequentially(value);
};
for (const language of ["en", "lv", "ar"] as const) {
  test(`partial leap-date entry survives the real Settings unmount in ${language}`, async ({
    page,
  }) => {
    const clean = await observe(page, true);
    await seed(page, { language });
    const reply = {
      outcome: {
        kind: "SUCCEEDED",
        data: {
          requester: actorContextInput({
            productVersion: "synthetic-presentation",
            observedAt: "2026-11-04T12:00:00.000000Z",
            businessDate: "2026-11-04",
            timeZone: "Etc/UTC",
            capabilities: ["REGISTER"],
            views: ["CASES"],
            offers: [{ kind: "OPEN" }],
          }),
        },
      },
    };
    expect(isResponse("context", reply)).toBe(true);
    await page.route("**/api/context", (route) => route.fulfill({ json: reply }));
    const requests: string[] = [];
    page.on("request", (request) => {
      if (new URL(request.url()).pathname.startsWith("/api/")) {
        requests.push(new URL(request.url()).pathname);
      }
    });
    await page.goto("/cases/new", { waitUntil: "commit" });
    const field = page.locator('[data-field-name="incidentDate"]');
    await expect(field).toBeVisible();
    const canonical = field.locator('input[type="hidden"]');
    await setPart(field, "year", "2024");
    await setPart(field, "month", "2");
    await setPart(field, "day", "29");
    await expect(canonical).toHaveValue("2024-02-29");
    const year = field.locator('[data-type="year"]');
    await year.focus();
    for (let index = 0; index < 4; index += 1) {
      await year.press("Delete");
    }
    await expect(year).not.toHaveAttribute("aria-valuenow");
    await expect(canonical).toHaveValue("");
    const before = [...requests];
    const previousField = await field.elementHandle();
    if (previousField === null) {
      throw new Error("The visible date field must exist.");
    }
    await page.locator('[role="dialog"] [data-open-settings]').click();
    await expect(page.locator(".settings-panel")).toBeVisible();
    expect(await previousField.evaluate((node) => node.isConnected)).toBe(false);
    await page.locator("[data-return-workspace]").click();
    await expect(field.locator('[data-type="day"]')).toHaveAttribute("aria-valuenow", "29");
    await expect(field.locator('[data-type="month"]')).toHaveAttribute("aria-valuenow", "2");
    await expect(field.locator('[data-type="year"]')).not.toHaveAttribute("aria-valuenow");
    await expect(canonical).toHaveValue("");
    expect(requests).toEqual(before);
    await setPart(field, "year", "2024");
    await expect(canonical).toHaveValue("2024-02-29");
    expect(requests.filter((path) => path.includes("operation."))).toEqual([]);
    await expectAccessible(page);
    await clean();
  });
}
