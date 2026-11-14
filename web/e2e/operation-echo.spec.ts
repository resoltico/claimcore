import { type ResponseByEndpoint } from "@contracts/client";
import { randomUUID } from "node:crypto";
import { expect, test } from "@playwright/test";
import { operationId } from "../src/operation/operationId";
import { startOpen } from "./case-workflow";
import { openAuthenticated } from "./session-helpers";
test("a mismatched operation echo retains the draft and cannot enable confirmation", async ({
  page,
}) => {
  await openAuthenticated(page);
  const reference = `ECHO-${randomUUID()}`;
  await startOpen(page, reference);
  let executions = 0;
  page.on("request", (request) => {
    if (request.url().endsWith("/api/operation.execute")) {
      executions += 1;
    }
  });
  await page.route("**/api/operation.preview", async (route) => {
    const response = await route.fetch();
    const operationPreviewReply =
      (await response.json()) as ResponseByEndpoint["operation.preview"];
    if (operationPreviewReply.outcome.kind !== "REVIEWED") {
      throw new Error("The real preview positive control must admit");
    }
    await route.fulfill({
      status: 200,
      json: {
        ...operationPreviewReply,
        operationId: operationId(),
      },
    });
  });
  await page
    .getByRole("button", {
      name: "Review changes",
      exact: true,
    })
    .click();
  await expect(page.getByRole("alert")).toContainText(
    "ClaimCore sent an unexpected response (HTTP 200).",
  );
  await expect(
    page.getByLabel("Case reference", {
      exact: true,
    }),
  ).toHaveValue(reference);
  await expect(
    page.getByRole("button", {
      name: "Review changes",
      exact: true,
    }),
  ).toBeEnabled();
  await expect(page.locator("[data-confirm-operation]")).toHaveCount(0);
  expect(executions).toBe(0);
});
