import { workBasis, principal } from "../support/inputs";
import type { ComponentProps } from "react";
import { expect, it, vi } from "vitest";
import { fireEvent, within } from "@testing-library/react";
import { render, screen } from "../support/presentation-test-support";
import { OperationReview } from "../../src/views/operation/OperationReview";
import { prepareRequest } from "../../src/protocol/preparedRequest";
import type { OperationInput, OperationReview as Review } from "@contracts/client";
import { preferenceKey } from "../../src/presentation/preferences";
import { operationId, review } from "../support/service-foundation.fixtures";
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
  kind: "CASE",
  operationId,
  target: { kind: "REFERENCE", caseReference: "CASE-1" },
  expectedRevision: 1,
  act: {
    kind: "CORRECT",
    groups: {
      registration: {
        kind: "UPDATE",
        values: { claimantName: "KEEP", claimed: { amount: "12.3400", currency: "EUR" } },
      },
      decision: {
        kind: "KEEP",
      },
      payment: {
        kind: "CLEAR",
      },
    },
  },
};
it("case review leads with its business target and retains the full exact request in details", () => {
  const operation = model(correction, review);
  const prepared = operation.prepared!;
  const before = prepared.bytes;
  render(<OperationReview model={operation} />);
  const dialog = screen.getByRole("dialog");
  expect(
    within(dialog).queryByText("Grant scope", {
      exact: true,
    }),
  ).toBeNull();
  expect(dialog).not.toHaveTextContent("One case");
  const request = dialog.querySelector<HTMLElement>(".review-request")!;
  fireEvent.click(request.querySelector("summary")!);
  expect(within(request).getByText("CASE-1", { exact: true })).toBeVisible();
  expect(
    within(request).getByText("12.3400", {
      exact: true,
    }),
  ).toBeVisible();
  expect(operation.prepared).toBe(prepared);
  expect(prepared.bytes).toBe(before);
  expect(prepared.input).toEqual(correction);
});
const authorityReviewModel = (kind: "INSTALLATION" | "CASE") => {
  const scope =
    kind === "CASE"
      ? {
          kind,
          target: { kind: "REFERENCE" as const, caseReference: "SYNTHETIC-GRANT" },
        }
      : {
          kind,
        };
  const resolvedScope =
    kind === "CASE"
      ? {
          kind,
          caseId: "20000000-0000-4000-8000-000000000001",
          caseReference: "SYNTHETIC-GRANT",
        }
      : { kind };
  const input: OperationInput = {
    kind: "AUTHORITY",
    operationId,
    target: principal({ subject: "KEEP" }),
    act: {
      kind: "GRANT",
      role: "CASE_READER",
      scope,
    },
  };
  const proposed: Review = {
    view: { kind: "RECORD" as const, sections: [] },
    presentation: { changes: [], unchanged: [] },
    subject: {
      kind: "AUTHORITY",
      basis: workBasis,
      effects: [],
      target: null,
      accessScope: resolvedScope,
      before: {
        enabled: true,
        profile: null,
        access: [],
        grants: [],
      },
      after: {
        enabled: true,
        profile: null,
        access: [],
        grants: [
          {
            role: "CASE_READER",
            scope: resolvedScope,
          },
        ],
      },
    },
  };
  return model(input, proposed);
};
it.each(["INSTALLATION", "CASE"] as const)(
  "authority review keeps nested %s grant scope and literal token-looking subject",
  (kind) => {
    render(<OperationReview model={authorityReviewModel(kind)} />);
    const dialog = screen.getByRole("dialog");
    fireEvent.click(dialog.querySelector(".review-request > summary")!);
    expect(
      within(dialog).getAllByText("Grant scope", {
        exact: true,
      }),
    ).toHaveLength(1);
    expect(
      within(dialog).getAllByText(kind === "CASE" ? "One case" : "All cases", {
        exact: true,
      }),
    ).toHaveLength(1);
    expect(
      within(dialog.querySelector<HTMLElement>(".review-request")!)
        .getByText("KEEP", {
          exact: true,
        })
        .closest("code"),
    ).toBeNull();
    expect(
      within(dialog).queryByText("Keep current details", {
        exact: true,
      }),
    ).toBeNull();
    expect(
      within(dialog).queryByText("AUTHORITY", {
        exact: true,
      }),
    ).toBeNull();
    if (kind === "CASE") {
      expect(
        within(dialog).getAllByText("SYNTHETIC-GRANT", {
          exact: true,
        }),
      ).toHaveLength(1);
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
  "correction review names each group and action in $language without structural container headings",
  ({ language, groups, action, keep }) => {
    localStorage.setItem(
      preferenceKey,
      JSON.stringify({
        version: 3,
        language,
        displayLocale: "en-GB",
        mode: "system" as const,
        accent: "green" as const,
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
    expect(
      within(dialog.querySelector<HTMLElement>(".review-values")!).queryByText(keep, {
        exact: true,
      }),
    ).toBeNull();
    expect(
      within(dialog.querySelector<HTMLElement>(".review-values")!).queryByText(action, {
        selector: "dt",
        exact: true,
      }),
    ).toBeNull();
    const request = dialog.querySelector<HTMLElement>(".review-request")!;
    fireEvent.click(request.querySelector("summary")!);
    const authored = within(dialog).getAllByText("KEEP", {
      exact: true,
    });
    expect(authored).toHaveLength(3);
    expect(authored.filter((value) => value.closest("code") === null)).toHaveLength(2);
    const headings = [...dialog.querySelectorAll("dt")].map((value) => value.textContent);
    for (const structural of ["values", "groups", "kind"]) {
      expect(headings).not.toContain(structural);
    }
  },
);
it("erasure review retains its act meaning and external-copy limitation", () => {
  render(
    <OperationReview
      model={model(
        {
          kind: "CASE",
          operationId,
          target: { kind: "REFERENCE", caseReference: "CASE-1" },
          expectedRevision: 1,
          act: {
            kind: "ERASE",
          },
        },
        review,
      )}
    />,
  );
  expect(screen.getByRole("dialog")).toHaveTextContent(/Copies held elsewhere are not affected/u);
});

it("shows the core's profile values in authority review before consent", () => {
  const input: OperationInput = {
    kind: "AUTHORITY",
    operationId,
    target: principal({ subject: "profile-target" }),
    act: {
      kind: "SET_PROFILE",
      profile: { displayName: "Requested name", contact: "Requested contact" },
    },
  };
  const proposed: Review = {
    view: { kind: "RECORD" as const, sections: [] },
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
    subject: {
      kind: "AUTHORITY",
      basis: workBasis,
      effects: [],
      target: null,
      accessScope: { kind: "INSTALLATION" },
      before: {
        enabled: true,
        profile: { displayName: "Recorded name", contact: "Recorded contact" },
        access: [],
        grants: [],
      },
      after: {
        enabled: true,
        profile: { displayName: "Core proposed name", contact: "Core proposed contact" },
        access: [],
        grants: [],
      },
    },
  };
  render(<OperationReview model={model(input, proposed)} />);
  const dialog = screen.getByRole("dialog");
  expect(within(dialog).getByText("Recorded name", { exact: true })).toBeVisible();
  expect(within(dialog).getByText("Core proposed name", { exact: true })).toBeVisible();
  expect(within(dialog).getByText("Recorded contact", { exact: true })).toBeVisible();
  expect(within(dialog).getByText("Core proposed contact", { exact: true })).toBeVisible();
});
