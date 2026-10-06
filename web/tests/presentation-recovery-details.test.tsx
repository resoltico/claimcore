import { expect, it, vi } from "vitest";
import { render, screen } from "./presentation-test-support";
import { preferenceKey } from "../src/presentation/preferences";
import { RecoveryDetailsDialog } from "../src/views/recovery/RecoveryPanels";
import type { Inspection, RecoveryActions } from "../src/views/recovery/RecoveryState";
import { operationId, preparation } from "./v3-ui.fixtures";
import { createPresenter } from "../src/presentation/presenter";

const actions: RecoveryActions = {
  inspectId: vi.fn(),
  inspect: vi.fn(),
  loadAttempts: vi.fn(),
  choose: vi.fn(),
  act: vi.fn(),
  exportItem: vi.fn(),
  preview: vi.fn(),
  retain: vi.fn(),
};
const authoredValues = [
  { name: "registration.action", value: "REPLACE" },
  { name: "decision.action", value: "KEEP" },
  { name: "payment.action", value: "CLEAR" },
  { name: "claimedAmount", value: "1.0000" },
  { name: "incidentDate", value: "0001-01-01" },
  { name: "claimantName", value: "A\u0308 العربية\u200D\u202E <script>" },
];
const retainedCorrection: Inspection = {
  tag: "RETAINED",
  value: {
    preparation: {
      ...preparation,
      authoredValues,
      summary: { ...preparation.summary, command: "CORRECT_CASE" },
      attempts: {
        items: [
          {
            attemptId: operationId,
            startedAt: "2026-09-10T00:00:00.0000000+00:00",
            settledAt: null,
            settlement: null,
          },
        ],
        nextCursor: null,
      },
    },
    observation: { tag: "NOT_FOUND", identity: operationId },
  },
};

it.each(["en", "lv", "ar"] as const)(
  "labels grouped recovery targets in %s while keeping exact authored values [CC-WEB-001]",
  (language) => {
    localStorage.setItem(
      preferenceKey,
      JSON.stringify({ version: 1, language, displayLocale: "ar-EG" }),
    );
    render(
      <RecoveryDetailsDialog selected={retainedCorrection} onClose={vi.fn()} actions={actions} />,
    );
    const p = createPresenter({ language, displayLocale: "ar-EG" });
    const dialog = screen.getByRole("dialog");
    for (const [index, name] of ["registration", "decision", "payment"].entries()) {
      expect(dialog.querySelectorAll("dt")[index]?.textContent).toBe(
        `${p.groupLabel(name)} · ${p.text("ui.action")}`,
      );
    }
    for (const entry of authoredValues) {
      expect(
        [...dialog.querySelectorAll("bdi")].some((element) => element.textContent === entry.value),
      ).toBe(true);
    }
    expect(dialog).not.toHaveTextContent("registration.action");
    expect(dialog).not.toHaveTextContent("decision.action");
    expect(dialog).not.toHaveTextContent("payment.action");
    expect(dialog).toHaveTextContent("U+200D, U+202E");
    expect(dialog.querySelector("script")).toBeNull();
    expect(
      [...dialog.querySelectorAll("bdi")].some(
        (element) => element.textContent === "2026-09-10T00:00:00.0000000+00:00",
      ),
    ).toBe(true);
  },
);
