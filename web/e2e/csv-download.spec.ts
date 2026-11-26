import { randomUUID } from "node:crypto";
import { readFile } from "node:fs/promises";
import { expect, test, type Locator, type Page } from "./fixtures";
import { settlePopupTriggerScroll, workspaceSettingsNavigation } from "./workspace-navigation";
import { openAuthenticated } from "./session-helpers";
import { selectLanguage } from "./localization-support";
import { openCase } from "./case-workflow";
import { applyPeople } from "./people-setup";
import { operationId } from "../src/operation/operationId";
import { csvExportTransport } from "@contracts/client";
const download = async (page: Page, button: Locator, filename: string) => {
  await expect(button).toBeEnabled();
  const exporting = page.waitForResponse(
    (response) => new URL(response.url()).pathname === csvExportTransport.path,
  );
  const saved = page.waitForEvent("download");
  await button.click();
  const [response, file] = await Promise.all([exporting, saved]);
  expect(response.status()).toBe(200);
  expect(response.headers()["content-type"]).toContain("text/csv");
  expect(response.headers()["content-encoding"]).toBe("identity");
  expect(file.suggestedFilename()).toBe(filename);
  try {
    const path = await file.path();
    if (path === null) {
      throw new Error("Completed CSV path required");
    }
    const bytes = await readFile(path);
    expect(bytes.length).toBe(Number(response.headers()["content-length"]));
    expect(bytes.length).toBeLessThanOrEqual(csvExportTransport.maximumResponseBytes);
    return { text: bytes.toString("utf8"), request: response.request().postDataJSON() as unknown };
  } finally {
    await file.delete();
  }
};
const rejectParkedDownload = async (
  page: Page,
  button: Locator,
  reopen?: () => Promise<Locator>,
) => {
  const held = Promise.withResolvers<void>();
  const release = Promise.withResolvers<void>();
  const completed = Promise.withResolvers<void>();
  await page.evaluate(() => {
    const create = URL.createObjectURL.bind(URL);
    URL.createObjectURL = (object) => {
      document.documentElement.dataset["lateExportBlob"] = "created";
      return create(object);
    };
  });
  const routePath = `**${csvExportTransport.path}`;
  await page.route(routePath, async (route) => {
    try {
      const response = await route.fetch();
      expect(response.status()).toBe(200);
      expect(response.headers()["content-type"]).toContain("text/csv");
      held.resolve();
      await release.promise;
      await route.fulfill({ response }).catch(() => undefined);
    } catch (error) {
      held.reject(error);
      throw error;
    } finally {
      completed.resolve();
    }
  });
  try {
    await button.click();
    await held.promise;
    await (await workspaceSettingsNavigation(page)).click();
    await expect(page).toHaveURL(/\/settings$/u);
    release.resolve();
    await completed.promise;
    await page.goBack();
    await expect(reopen === undefined ? button : await reopen()).toBeEnabled();
    expect(await page.locator("html").getAttribute("data-late-export-blob")).toBeNull();
  } finally {
    release.resolve();
    await page.unroute(routePath);
  }
};

test("downloads complete filtered CSV bytes for the four existing query selections @engine", async ({
  page,
}) => {
  test.setTimeout(120_000);
  await openAuthenticated(page);
  await selectLanguage(page, "en");
  const reference = `CSV-${randomUUID().slice(0, 8)}`;
  await openCase(page, reference);
  await applyPeople(page, {
    operationId: operationId(),
    task: "reminder.create",
    binding: { kind: "REMINDER", target: { kind: "REFERENCE", caseReference: reference } },
    inputs: { "input:reminderAction": "+CSV α reminder", "input:reminderDueDate": "2027-01-01" },
  });
  await page.reload();
  const timeline = page.locator(".case-timeline-panel");
  await timeline.getByRole("tab", { name: "Work and reminders", exact: true }).click();
  const history = await download(
    page,
    timeline.getByRole("button", { name: "Download CSV" }),
    "claimcore-case-timeline.csv",
  );
  expect(history.text).toContain(reference);
  expect(history.text).toContain("'+CSV α reminder");
  expect(history.request).toMatchObject({ query: "case.timeline", selection: { filter: "WORK" } });
  await rejectParkedDownload(page, timeline.getByRole("button", { name: "Download CSV" }));
  await page.goto(`/cases?view=ALL&q=${encodeURIComponent(reference)}`);
  const showCaseCsv = async () => {
    await page.getByRole("button", { name: "Export", exact: true }).click();
    return page.getByRole("menuitem", { name: "Case list (CSV)", exact: true });
  };
  const cases = await download(page, await showCaseCsv(), "claimcore-cases.csv");
  expect(cases.text).toContain(reference);
  expect(cases.request).toEqual({
    query: "case.list",
    selection: { view: "ALL", text: reference },
  });
  await rejectParkedDownload(page, await showCaseCsv(), showCaseCsv);

  await page.goto(`/cases/report?view=ALL&q=${encodeURIComponent(reference)}`);
  const grouping = page.getByRole("combobox", { name: "Group by" });
  await settlePopupTriggerScroll(grouping);
  await grouping.press("Home");
  await expect(page.getByRole("listbox")).toBeVisible();
  await page.keyboard.press("ArrowDown");
  await expect(page.getByRole("option", { name: "Registered month" })).toBeFocused();
  await page.keyboard.press("Enter");
  const reportButton = page.getByRole("button", { name: "Export report CSV" });
  const report = await download(page, reportButton, "claimcore-case-summary.csv");
  expect(report.request).toEqual({
    query: "case.summary",
    selection: { view: "ALL", text: reference, groupBy: "REGISTERED_MONTH" },
  });
  await rejectParkedDownload(page, reportButton);

  await page.goto(
    `/reminders?filters=manual&state=PENDING&caseReference=${encodeURIComponent(reference)}`,
  );
  const reminders = await download(
    page,
    page.getByRole("button", { name: "Download CSV" }),
    "claimcore-reminders.csv",
  );
  expect(reminders.text).toContain(reference);
  expect(reminders.text).toContain("'+CSV α reminder");
  expect(reminders.request).toEqual({
    query: "reminder.list",
    selection: { target: { kind: "REFERENCE", caseReference: reference }, state: "PENDING" },
  });
  await page.goto("/people-history");
  const people = await download(
    page,
    page.getByRole("button", { name: "Download CSV" }),
    "claimcore-people-history.csv",
  );
  expect(people.text.startsWith("operationId,sequence,effectIndex,")).toBe(true);
  expect(people.request).toEqual({ query: "people.history", selection: {} });
  await rejectParkedDownload(page, page.getByRole("button", { name: "Download CSV" }));
});
