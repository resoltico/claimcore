import type { ComponentProps } from "react";
import { expect, it, vi } from "vitest";
import { within } from "@testing-library/react";
import { render, screen } from "./presentation-test-support";
import { OperationReview } from "../src/views/operation/OperationReview";
import { prepareRequest } from "../src/protocol/preparedRequest";
import type { OperationInput, OperationReview as Review } from "@contracts/client";
import { preferenceKey } from "../src/presentation/preferences";
import { operationId, review } from "./service-foundation.fixtures";

type Model = ComponentProps<typeof OperationReview>["model"];
const model = (input: OperationInput, proposed: Review): Model => ({
  prepared: prepareRequest("operation.execute", input),
  review: proposed,
  phase: "REVIEWING",
  busy: false,
  confirmed: false,
  confirm: vi.fn(),
  execute: vi.fn().mockResolvedValue(undefined),
  returnToEdit: vi.fn(),
});
const correction: OperationInput = {
  scope: "CASE",
  operationId,
  caseReference: "CASE-1",
  expectedRevision: 1,
  act: {
    kind: "CORRECT",
    groups: {
      registration: {
        mode: "REPLACE",
        values: {
          incidentDate: "2026-09-01",
          incidentNotificationDate: "2026-09-02",
          incidentCountry: "LV",
          claimantName: "KEEP",
          insurerName: "Synthetic insurer",
          claimedAmount: "12.3400",
          claimedCurrency: "EUR",
        },
      },
      decision: { mode: "KEEP" },
      payment: { mode: "CLEAR" },
    },
  },
};
it("case review omits only its root namespace and preserves exact prepared bytes", () => {
  const operation = model(correction, review);
  const prepared = operation.prepared!;
  const before = prepared.bytes;
  render(<OperationReview model={operation} />);
  const dialog = screen.getByRole("dialog");
  expect(within(dialog).queryByText("Grant scope", { exact: true })).toBeNull();
  expect(dialog).not.toHaveTextContent("One case");
  expect(within(dialog).getByText("CASE-1", { exact: true })).toBeVisible();
  expect(within(dialog).getByText("12.3400", { exact: true })).toBeVisible();
  expect(operation.prepared).toBe(prepared);
  expect(prepared.bytes).toBe(before);
  expect(prepared.input).toEqual(correction);
});
it.each(["INSTALLATION", "CASE"] as const)(
  "authority review keeps nested %s grant scope and literal token-looking subject",
  (kind) => {
    const scope = kind === "CASE" ? { kind, caseReference: "SYNTHETIC-GRANT" } : { kind };
    const input: OperationInput = {
      scope: "AUTHORITY",
      operationId,
      target: { kind: "HUMAN", issuer: "https://issuer.example/", subject: "KEEP" },
      act: { kind: "GRANT", role: "CASE_READER", scope },
    };
    const proposed: Review = {
      scope: "AUTHORITY",
      target: null,
      before: { enabled: true, grants: [] },
      after: { enabled: true, grants: [{ role: "CASE_READER", scope }] },
    };
    render(<OperationReview model={model(input, proposed)} />);
    const dialog = screen.getByRole("dialog");
    expect(within(dialog).getAllByText("Grant scope", { exact: true })).toHaveLength(2);
    expect(
      within(dialog).getAllByText(kind === "CASE" ? "One case" : "Installation", { exact: true }),
    ).toHaveLength(2);
    expect(within(dialog).getByText("KEEP", { exact: true }).closest("code")).toBeNull();
    expect(within(dialog).queryByText("Keep current details", { exact: true })).toBeNull();
    expect(within(dialog).queryByText("AUTHORITY", { exact: true })).toBeNull();
    if (kind === "CASE") {
      expect(within(dialog).getAllByText("SYNTHETIC-GRANT", { exact: true })).toHaveLength(2);
    }
  },
);
const localized = [
  {
    language: "en",
    groups: ["Registration", "Payment decision", "Payment record"],
    action: "Action",
    keep: "Keep current details",
  },
  {
    language: "lv",
    groups: ["Reģistrācija", "Izmaksas lēmums", "Maksājuma ieraksts"],
    action: "Darbība",
    keep: "Saglabāt pašreizējos datus",
  },
  {
    language: "ar",
    groups: ["التسجيل", "قرار الدفع", "سجل الدفع"],
    action: "الإجراء",
    keep: "إبقاء التفاصيل الحالية",
  },
] as const;
it.each(localized)(
  "correction review names each group and action in $language without structural container headings",
  ({ language, groups, action, keep }) => {
    localStorage.setItem(
      preferenceKey,
      JSON.stringify({ version: 1, language, displayLocale: "en-GB" }),
    );
    render(<OperationReview model={model(correction, review)} />);
    const dialog = screen.getByRole("dialog");
    for (const label of groups) {
      const heading = within(dialog).getByText(label, { selector: "dt", exact: true });
      expect(
        within(heading.parentElement!).getByText(action, { selector: "dt", exact: true }),
      ).toBeVisible();
    }
    expect(within(dialog).getByText(keep, { exact: true })).toBeVisible();
    const authored = within(dialog).getAllByText("KEEP", { exact: true });
    expect(authored).toHaveLength(2);
    expect(authored.filter((value) => value.closest("code") === null)).toHaveLength(1);
    const headings = [...dialog.querySelectorAll("dt")].map((value) => value.textContent);
    for (const structural of ["values", "groups", "mode"]) {
      expect(headings).not.toContain(structural);
    }
  },
);
it("erasure review retains its act meaning and WAL/backups limitation", () => {
  render(
    <OperationReview
      model={model(
        {
          scope: "CASE",
          operationId,
          caseReference: "CASE-1",
          expectedRevision: 1,
          act: { kind: "ERASE" },
        },
        review,
      )}
    />,
  );
  expect(screen.getByRole("dialog")).toHaveTextContent(
    /WAL, backups and copies elsewhere are not erased/u,
  );
});
