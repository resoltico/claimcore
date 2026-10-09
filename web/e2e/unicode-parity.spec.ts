import { randomUUID } from "node:crypto";
import { writeFile } from "node:fs/promises";
import { resolve } from "node:path";
import { expect, test } from "@playwright/test";
import { startOpen, prepare, recordPrepared } from "./case-workflow";
import { openAuthenticated, expectAccessible } from "./session-helpers";

test("records supplementary scalar boundaries through Web for published native readback [CC-CLI-001][CC-WEB-001]", async ({
  page,
}) => {
  await openAuthenticated(page);
  const reference = randomUUID().replaceAll("-", "") + "🙂".repeat(48);
  await startOpen(page, reference);
  const name = "🙂".repeat(200);
  await page.getByLabel("Country of incident", { exact: true }).fill("🙂".repeat(100));
  await page.getByLabel("Claimant name", { exact: true }).fill(name);
  await page.getByLabel("Responsible insurer", { exact: true }).fill(name);
  const identity = await prepare(page);
  await expectAccessible(page);
  await recordPrepared(page);
  await expect(page.locator("section.receipt section.case-fields")).toContainText(name);
  await expect(page.locator("section.receipt section.case-fields")).toContainText(reference);
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
