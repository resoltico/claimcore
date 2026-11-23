import { randomUUID } from "node:crypto";
import { expect, test } from "./fixtures";
import { openAuthenticated } from "./session-helpers";
import { openCase } from "./case-workflow";

test("Find uses a modifier shortcut, debounces suggestions and routes exact and free text", async ({
  page,
}) => {
  await openAuthenticated(page);
  const reference = `FIND-${randomUUID()}`;
  await openCase(page, reference);
  const apple = await page.evaluate(() => /Mac|iPhone|iPad|iPod/u.test(navigator.platform));
  await page.keyboard.press("k");
  await expect(page.getByRole("dialog")).toHaveCount(0);
  await page.keyboard.press(apple ? "Meta+k" : "Control+k");
  const input = page.getByRole("dialog").getByRole("combobox", { name: "Find a case" });
  await expect(input).toBeFocused();
  const queries: Readonly<Record<string, unknown>>[] = [];
  page.on("request", (request) => {
    if (new URL(request.url()).pathname !== "/api/case.list") {
      return;
    }
    const payload = request.postDataJSON() as Readonly<Record<string, unknown>>;
    if (payload["limit"] === 8) {
      queries.push(payload);
    }
  });
  await input.fill(reference.slice(0, 3));
  await input.fill(reference);
  await expect.poll(() => queries).toEqual([{ view: "ALL", text: reference, limit: 8 }]);
  await input.press("Enter");
  await expect(page.getByRole("dialog")).toHaveCount(0);
  await expect(page).toHaveURL(/\/cases\/id\//u);
  await page.keyboard.press(apple ? "Meta+k" : "Control+k");
  await input.fill("Synthetic claimant");
  await input.press("Enter");
  await expect(page).toHaveURL(
    (url) =>
      url.pathname === "/cases" &&
      url.searchParams.get("view") === "ALL" &&
      url.searchParams.get("text") === "Synthetic claimant",
  );
});
