import { activeRequester, requesterLinks } from "../tests/support/human-requester.fixtures";
import { humanReadReply } from "../tests/support/human-read-reply.fixtures";
import { geometryFailures } from "./design-geometry";
import { expect, test } from "./fixtures";
import { observe, seed } from "./presentation-probe";

test("short desktop keeps the full administration navigation and footer keyboard reachable @engine", async ({
  page,
}) => {
  const clean = await observe(page, true);
  await seed(page);
  await page.route("**/api/context", (route) =>
    route.fulfill({
      json: humanReadReply("context", {
        ...activeRequester,
        basis: {
          kind: "SNAPSHOT",
          observation: {
            observedAt: "2026-11-04T12:00:00.000000Z",
            businessDate: "2026-11-04",
            timeZone: "Etc/UTC",
          },
        },
        links: [
          ...requesterLinks(["CASES", "PEOPLE"], ["people.history"]),
          {
            rel: "VIEW",
            labelId: "endpoint.disclosure.trail.label",
            link: { endpoint: "disclosure.trail", input: { limit: 50 } },
          },
        ],
        screen: {
          regions: [
            {
              role: "FACTS",
              content: {
                kind: "ENTRIES",
                entries: [
                  {
                    key: "businessDate",
                    labelId: "label.businessDate",
                    prominence: "PRIMARY",
                    value: { kind: "DATE", value: "2026-11-04" },
                  },
                  {
                    key: "timeZone",
                    labelId: "label.timeZone",
                    prominence: "PRIMARY",
                    value: { kind: "TEXT", value: "Etc/UTC" },
                  },
                ],
              },
            },
          ],
        },
      }),
    }),
  );
  await page.setViewportSize({ width: 1440, height: 480 });
  await page.goto("/settings");
  const sidebar = page.locator(".workspace-sidebar");
  const navigation = sidebar.getByRole("navigation");
  const footer = sidebar.locator(".shell-footer");
  const paths = ["/cases", "/reminders", "/people", "/people-history", "/disclosure-trail"];
  await expect(navigation.getByRole("link")).toHaveCount(paths.length);
  await expect(footer.locator(".shell-business-date")).toBeVisible();
  await page.evaluate(async () => {
    await document.fonts.ready;
  });
  expect(await geometryFailures(page)).not.toContain("G2");
  await page.setViewportSize({ width: 1440, height: 400 });
  await expect
    .poll(() => navigation.evaluate((nav) => nav.scrollHeight > nav.clientHeight))
    .toBe(true);

  const find = sidebar.locator("#workspace-find");
  await find.focus();
  await page.keyboard.press("Tab");
  // Firefox includes the overflowing navigation itself in the tab sequence.
  if (await navigation.evaluate((nav) => nav === document.activeElement)) {
    await page.keyboard.press("Tab");
  }
  for (const path of paths) {
    const link = navigation.locator(`a[href="${path}"]`);
    await expect(link).toBeFocused();
    await expect(link).toBeInViewport({ ratio: 1 });
    await page.keyboard.press("Tab");
  }
  await expect(footer.getByRole("link", { name: "About ClaimCore" })).toBeFocused();
  await page.keyboard.press("Tab");
  const account = footer.locator("#workspace-account");
  await expect(account).toBeFocused();
  await expect(footer).toBeInViewport({ ratio: 1 });
  await account.press("Enter");
  await expect(page.getByRole("menuitem", { name: "Sign out" })).toBeVisible();
  await page.keyboard.press("Escape");
  await expect(account).toBeFocused();
  await page.setViewportSize({ width: 1440, height: 600 });
  expect(await geometryFailures(page)).not.toContain("G2");
  await clean();
});
