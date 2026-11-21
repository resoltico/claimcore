import { fillNamedField } from "./typed-entry";
import { randomUUID } from "node:crypto";
import { writeFile } from "node:fs/promises";
import { resolve } from "node:path";
import { expect, test } from "./fixtures";
import { startOpen, reviewDraft, recordReviewed } from "./case-workflow";
import { openAuthenticated, expectAccessible } from "./session-helpers";

test("records supplementary scalar boundaries through Web for published native readback", async ({
  page,
}) => {
  await openAuthenticated(page);
  const reference = `UNICODE-${randomUUID()}`;
  await startOpen(page, reference);
  const name = "🙂".repeat(80);
  await fillNamedField(page, "Country", "LT");
  await page.getByLabel("Policy number", { exact: true }).fill(name);
  await page.getByLabel("Insurer claim reference", { exact: true }).fill(name);
  const identity = await reviewDraft(page);
  await expectAccessible(page);
  await recordReviewed(page);
  await expect(
    page.locator(
      "section.case-detail [data-region=FACTS] .entries-view [data-entry='policyNumber']",
    ),
  ).toContainText(name);
  await expect(page.locator("#case-detail-title")).toHaveText(reference);
  const output = process.env["CLAIMCORE_WEB_E2E_PRIVATE_OUTPUT_DIR"];
  if (output === undefined) {
    throw new Error("Private cross-client fixture output is missing.");
  }
  await writeFile(
    resolve(output, "unicode-case.json"),
    JSON.stringify({ ...identity, reference }),
    { mode: 0o600, flag: "wx" },
  );
});
