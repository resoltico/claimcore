import { expectCaseReadsSettled } from "./case-readiness";
import { join } from "node:path";
import { matrixEvidenceDirectory } from "./design-matrix-checks";
import { randomUUID } from "node:crypto";
import { expect, test } from "@playwright/test";
import { openAuthenticated, expectAccessible } from "./session-helpers";
import { openCase } from "./case-workflow";
import { noHorizontalOverflow } from "./layout-support";

test("notes use reviewed unversioned writes, latest three, paging, removal and Activity @engine", async ({
  page,
}) => {
  await openAuthenticated(page);
  await openCase(page, `NOTE-${randomUUID()}`);
  const notes = page.locator('.case-detail [data-region="NOTES"]');
  let writes = 0;
  page.on("request", (request) => {
    if (new URL(request.url()).pathname === "/api/operation.execute") {
      writes += 1;
    }
  });
  for (let number = 1; number <= 4; number += 1) {
    await notes
      .getByRole("textbox", { name: "Note", exact: true })
      .fill(`Handling note ${number}\nSecond line`);
    const preview = page.waitForResponse(
      (response) => new URL(response.url()).pathname === "/api/operation.preview",
    );
    await notes.getByRole("button", { name: "Add note", exact: true }).click();
    const reviewed = await preview;
    expect(reviewed.request().postDataJSON()).toMatchObject({
      task: "note.add",
      binding: { kind: "NOTE" },
      inputs: { "input:noteText": `Handling note ${number}\nSecond line` },
    });
    expect(reviewed.request().postDataJSON()).not.toHaveProperty("expectedRevision");
    const dialog = page.getByRole("dialog");
    const confirm = dialog.locator("button[data-confirm-operation]");
    await expect(confirm).toBeEnabled();
    expect(writes).toBe(number - 1);
    await confirm.click();
    await expect(dialog).toHaveCount(0);
    await expectCaseReadsSettled(page);
    await expect(notes.getByText(`Handling note ${number}`, { exact: false })).toBeVisible();
  }
  await expect(notes.locator(".note-list > li")).toHaveCount(3);
  await expect(notes).not.toContainText("Handling note 1");
  await notes.getByRole("button", { name: "Show all notes", exact: true }).click();
  await expect(notes.locator(".note-list > li")).toHaveCount(4);
  const removed = notes.locator(".note-list > li").filter({ hasText: "Handling note 2" });
  await removed.getByRole("button", { name: "Remove note", exact: true }).click();
  const dialog = page.getByRole("dialog");
  const confirm = dialog.locator("button[data-confirm-operation]");
  await expect(confirm).toBeEnabled();
  expect(writes).toBe(4);
  await expect(dialog.getByRole("textbox", { name: "Note ID", exact: true })).toHaveCount(0);
  await expectAccessible(page);
  await confirm.click();
  await expect(dialog).toHaveCount(0);
  await expectCaseReadsSettled(page);
  await expect(notes).not.toContainText("Handling note 2");
  expect(writes).toBe(5);
  const filtered = page.waitForResponse((response) => {
    if (new URL(response.url()).pathname !== "/api/case.timeline") {
      return false;
    }
    const input: unknown = response.request().postDataJSON();
    return (
      typeof input === "object" && input !== null && "filter" in input && input.filter === "NOTES"
    );
  });
  const activity = page.locator(".case-timeline-panel");
  const tab = activity.getByRole("tab", { name: "Notes", exact: true });
  await tab.click();
  await filtered;
  await expectCaseReadsSettled(page);
  await expect(tab).toHaveAttribute("aria-selected", "true");
  await expect(activity.locator(".region-timeline > li")).toHaveCount(5);
  await expect(activity).toContainText("Note removed");
  await expect(activity).not.toContainText("Case opened");
  const evidence = await matrixEvidenceDirectory();
  await page.screenshot({ path: join(evidence, "notes-en-1280.png"), fullPage: true });
  await page.setViewportSize({ width: 320, height: 900 });
  await noHorizontalOverflow(page, "narrow");
  await expectAccessible(page);
  await page.screenshot({ path: join(evidence, "notes-en-320.png"), fullPage: true });
  process.stdout.write(`Notes visual evidence: ${evidence}\n`);
});
