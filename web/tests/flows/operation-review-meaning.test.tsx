import type { ComponentProps } from "react";
import { expect, it, vi } from "vitest";
import { fireEvent, within } from "@testing-library/react";
import { render, screen } from "../support/presentation-test-support";
import { OperationReview } from "../../src/views/operation/OperationReview";
import { prepareRequest } from "../../src/protocol/preparedRequest";
import type { OperationInput, HumanReview, PresentedEntry, Screen } from "@contracts/client";
import { preferenceKey } from "../../src/presentation/preferences";
import { operationId } from "../support/service-foundation.fixtures";
import { closeReview } from "../support/operation-documents.fixtures";

type Model = ComponentProps<typeof OperationReview>["model"];
const model = (input: OperationInput, review: HumanReview): Model => ({
  prepared: prepareRequest("operation.execute", input),
  review,
  phase: "REVIEWING",
  busy: false,
  confirmed: false,
  confirm: vi.fn(),
  execute: vi.fn().mockResolvedValue(undefined),
  returnToEdit: vi.fn(),
});
const entry = (key: string, labelId: string, value: PresentedEntry["value"]): PresentedEntry => ({
  key,
  labelId,
  value,
  prominence: "PRIMARY",
});
const details = (entries: ReadonlyArray<PresentedEntry>): Screen["regions"][number] => ({
  role: "DETAILS",
  titleId: "label.recordDetails",
  content: { kind: "ENTRIES", entries },
});
const correction: OperationInput = {
  kind: "CASE",
  operationId,
  target: { kind: "REFERENCE", caseReference: "CASE-1" },
  expectedRevision: 1,
  act: {
    kind: "CORRECT",
    groups: {
      registration: {
        kind: "UPDATE",
        values: { claimantName: "KEEP", claimedAmount: "12.3400", claimedCurrency: "EUR" },
      },
      decision: { kind: "KEEP" },
    },
  },
};
const review: HumanReview = {
  ...closeReview,
  document: {
    ...closeReview.document,
    context: {
      ...closeReview.document.context,
      task: "CASE:CORRECT",
      target: { kind: "CASE", caseId: "20000000-0000-4000-8000-000000000001", beforeRevision: 1 },
      confirmation: { kind: "CASE_REFERENCE", reference: "CASE-1" },
      inputs: [
        "input:operationId",
        "field:caseReference",
        "input:expectedRevision",
        "group:registration",
        "group:decision",
        "field:incidentDate",
        "field:incidentNotificationDate",
        "field:incidentCountry",
        "field:incidentDescription",
        "field:policyNumber",
        "field:insurerClaimReference",
        "field:claimantName",
        "field:insurerName",
        "field:claimedAmount",
        "field:claimedCurrency",
        "field:paymentDecisionDate",
        "field:payableAmount",
        "field:payableCurrency",
      ],
    },
    screen: {
      regions: [
        {
          role: "IDENTITY",
          content: {
            kind: "ENTRIES",
            entries: [
              entry("caseReference", "field.caseReference.label", {
                kind: "REFERENCE",
                value: "CASE-1",
              }),
            ],
          },
        },
        details([
          entry("caseReference", "field.caseReference.label", {
            kind: "REFERENCE",
            value: "CASE-1",
          }),
          entry("authoredAmount", "field.claimedAmount.label", { kind: "TEXT", value: "12.3400" }),
        ]),
      ],
    },
  },
};
it("preserves Core-supplied target and technical values without changing the prepared command", () => {
  const operation = model(correction, review);
  const prepared = operation.prepared!;
  const { bytes } = prepared;
  render(<OperationReview model={operation} />);
  const dialog = screen.getByRole("dialog");
  expect(dialog.querySelector('[data-region="IDENTITY"]')).toHaveTextContent("CASE-1");
  expect(within(dialog).queryByText("Grant scope", { exact: true })).toBeNull();
  const request = dialog.querySelector<HTMLElement>(".screen-details")!;
  fireEvent.click(request.querySelector("summary")!);
  expect(within(request).getByText("CASE-1", { exact: true })).toBeVisible();
  expect(within(request).getByText("12.3400", { exact: true })).toBeVisible();
  expect(operation.prepared).toBe(prepared);
  expect(prepared.bytes).toBe(bytes);
  expect(prepared.input).toEqual(correction);
});

const authorityReviewModel = (kind: "INSTALLATION" | "CASE") => {
  const scope =
    kind === "CASE"
      ? { kind, target: { kind: "REFERENCE" as const, caseReference: "SYNTHETIC-GRANT" } }
      : { kind };
  const input: OperationInput = {
    kind: "AUTHORITY",
    operationId,
    target: { kind: "HUMAN", issuer: "https://issuer.example/", subject: "KEEP" },
    act: { kind: "GRANT", role: "CASE_READER", scope },
  };
  const proposed: HumanReview = {
    ...closeReview,
    document: {
      ...closeReview.document,
      context: {
        kind: "REVIEW",
        task: "AUTHORITY:GRANT",
        consequence: "ROUTINE",
        confirmation: { kind: "OPERATION", operationId },
        inputs: [
          "input:operationId",
          "input:principal.kind",
          "input:principal.issuer",
          "input:principal.subject",
          "input:role",
          "input:scope",
          "field:caseReference",
        ],
        target: {
          kind: "AUTHORITY",
          actorId: "10000000-0000-4000-8000-000000000099",
          scope:
            kind === "CASE"
              ? {
                  kind,
                  caseId: "20000000-0000-4000-8000-000000000001",
                  caseReference: "SYNTHETIC-GRANT",
                }
              : { kind },
        },
      },
      screen: {
        regions: [
          details([
            entry("scope", "input.scope.label", { kind: "CODE", vocabulary: "scope", token: kind }),
            entry("subject", "field.principal.subject.label", { kind: "TEXT", value: "KEEP" }),
            ...(kind === "CASE"
              ? [
                  entry("caseReference", "field.caseReference.label", {
                    kind: "REFERENCE",
                    value: "SYNTHETIC-GRANT",
                  }),
                ]
              : []),
          ]),
        ],
      },
    },
  };
  return model(input, proposed);
};
it.each(["INSTALLATION", "CASE"] as const)(
  "authority review displays supplied %s scope and literal token-looking subject",
  (kind) => {
    render(<OperationReview model={authorityReviewModel(kind)} />);
    const dialog = screen.getByRole("dialog");
    const request = dialog.querySelector<HTMLElement>(".screen-details")!;
    fireEvent.click(request.querySelector("summary")!);
    expect(within(dialog).getAllByText("Grant scope", { exact: true })).toHaveLength(1);
    expect(
      within(dialog).getAllByText(kind === "CASE" ? "One case" : "All cases", { exact: true }),
    ).toHaveLength(1);
    expect(within(request).getByText("KEEP", { exact: true }).closest("code")).toBeNull();
    expect(within(dialog).queryByText("Keep current details", { exact: true })).toBeNull();
    expect(within(dialog).queryByText("AUTHORITY", { exact: true })).toBeNull();
    if (kind === "CASE") {
      expect(within(dialog).getAllByText("SYNTHETIC-GRANT", { exact: true })).toHaveLength(1);
    }
  },
);
const localized = [
  {
    language: "en",
    groups: ["Claim", "Payment decision", "Payment"],
    action: "Action",
    keep: "Keep current details",
  },
  {
    language: "lv",
    groups: ["Prasījums", "Izmaksas lēmums", "Maksājums"],
    action: "Darbība",
    keep: "Saglabāt pašreizējos datus",
  },
  {
    language: "ar",
    groups: ["المطالبة", "قرار الدفع", "الدفع"],
    action: "الإجراء",
    keep: "إبقاء التفاصيل الحالية",
  },
] as const;
it.each(localized)(
  "correction review renders supplied group labels in $language and treats authored text literally",
  ({ language, groups, action, keep }) => {
    localStorage.setItem(
      preferenceKey,
      JSON.stringify({
        version: 3,
        language,
        displayLocale: "en-GB",
        mode: "system",
        accent: "green",
      }),
    );
    render(
      <OperationReview
        model={model(correction, {
          ...review,
          presentation: {
            changes: [
              {
                key: "claimant",
                labelId: "field.claimantName.label",
                before: { kind: "PARTY", value: "Earlier name" },
                after: { kind: "PARTY", value: "KEEP" },
              },
            ],
            unchanged: [
              { key: "registration", labelId: "group.registration.label" },
              { key: "decision", labelId: "group.decision.label" },
              { key: "payment", labelId: "group.payment.label" },
            ],
          },
        })}
      />,
    );
    const dialog = screen.getByRole("dialog");
    const unchanged = dialog.querySelector<HTMLElement>(".review-unchanged")!;
    for (const label of groups) {
      expect(unchanged).toHaveTextContent(label);
    }
    const values = dialog.querySelector<HTMLElement>(".review-values")!;
    expect(within(values).queryByText(keep, { exact: true })).toBeNull();
    expect(within(values).queryByText(action, { selector: "dt", exact: true })).toBeNull();
    expect(within(values).getByText("KEEP", { exact: true }).closest("code")).toBeNull();
    const headings = [...dialog.querySelectorAll("dt")].map((value) => value.textContent);
    for (const structural of ["values", "groups", "kind"]) {
      expect(headings).not.toContain(structural);
    }
  },
);
it("renders the Core-authored erasure meaning and external-copy limitation", () => {
  const input: OperationInput = {
    kind: "CASE",
    operationId,
    target: { kind: "REFERENCE", caseReference: "CASE-1" },
    expectedRevision: 1,
    act: { kind: "ERASE" },
  };
  const proposed: HumanReview = {
    ...closeReview,
    document: {
      ...closeReview.document,
      context: {
        ...closeReview.document.context,
        task: "CASE:ERASE",
        consequence: "IRREVERSIBLE",
        target: { kind: "CASE", caseId: "20000000-0000-4000-8000-000000000001", beforeRevision: 1 },
        confirmation: { kind: "CASE_REFERENCE", reference: "CASE-1" },
      },
      screen: {
        regions: [
          {
            role: "NOTICE",
            content: { kind: "NOTICE", messageId: "act.ERASE.meaning", tone: "FINAL" },
          },
        ],
      },
    },
  };
  render(<OperationReview model={model(input, proposed)} />);
  expect(screen.getByRole("dialog")).toHaveTextContent(/Copies held elsewhere are not affected/u);
});
it("shows Core profile changes rather than recomputing proposed values from authored input", () => {
  const input: OperationInput = {
    kind: "AUTHORITY",
    operationId,
    target: { kind: "HUMAN", issuer: "https://issuer.example/", subject: "profile-target" },
    act: {
      kind: "SET_PROFILE",
      profile: { displayName: "Requested name", contact: "Requested contact" },
    },
  };
  const proposed: HumanReview = {
    ...closeReview,
    document: {
      ...closeReview.document,
      context: {
        kind: "REVIEW",
        task: "AUTHORITY:SET_PROFILE",
        consequence: "ROUTINE",
        inputs: [
          "input:operationId",
          "input:principal.kind",
          "input:principal.issuer",
          "input:principal.subject",
          "input:profile.displayName",
          "input:profile.contact",
        ],
        target: {
          kind: "AUTHORITY",
          actorId: "10000000-0000-4000-8000-000000000099",
          scope: { kind: "INSTALLATION" },
        },
        confirmation: { kind: "OPERATION", operationId },
      },
    },
    presentation: {
      changes: [
        {
          key: "profileName",
          labelId: "field.profile.displayName.label",
          before: { kind: "PARTY", value: "Recorded name" },
          after: { kind: "PARTY", value: "Core proposed name" },
        },
        {
          key: "profileContact",
          labelId: "field.profile.contact.label",
          before: { kind: "TEXT", value: "Recorded contact" },
          after: { kind: "TEXT", value: "Core proposed contact" },
        },
      ],
      unchanged: [],
    },
  };
  render(<OperationReview model={model(input, proposed)} />);
  const dialog = screen.getByRole("dialog");
  for (const text of [
    "Recorded name",
    "Core proposed name",
    "Recorded contact",
    "Core proposed contact",
  ]) {
    expect(within(dialog).getByText(text, { exact: true })).toBeVisible();
  }
  expect(within(dialog).queryByText("Requested name", { exact: true })).toBeNull();
  expect(within(dialog).queryByText("Requested contact", { exact: true })).toBeNull();
});
