import { prefixedInputs } from "../../support/submission-inputs";
import { operationAffordance } from "../../support/affordances.fixtures";
import { afterEach, expect, it, vi } from "vitest";
import userEvent from "@testing-library/user-event";
import type { HumanReadDocument_case_get, Region, PresentedEntry } from "@contracts/client";
import { render, screen, TestSession, waitFor } from "../../support/presentation-test-support";
import { CaseDetail } from "../../../src/views/CaseDetail";
import { productMetadata } from "../../../src/protocol/productMetadata";
const caseId = "00000000-0000-4000-8000-000000000001";
const paymentId = "019aa6a0-0000-7000-8000-000000000002";
const reminderId = "00000000-0000-4000-8000-000000000003";
const entry = (key: string, caption: string): PresentedEntry => ({
  key,
  labelId: "label.details",
  prominence: "PRIMARY",
  value: {
    kind: "TEXT",
    value: caption,
  },
});
const table = (role: Region["role"], key: string, caption: string): Region => ({
  role,
  content: {
    kind: "TABLE",
    columns: [
      {
        key: "caption",
        labelId: "label.details",
        prominence: "PRIMARY",
        isMoney: false,
      },
    ],
    rows: [
      {
        key,
        actions:
          role === "PAYMENTS"
            ? [
                operationAffordance("case.removePayment", {
                  kind: "CASE",
                  target: { kind: "ID", caseId },
                  expectedRevision: 4,
                  paymentId: key,
                }),
              ]
            : [
                operationAffordance("reminder.complete", {
                  kind: "REMINDER",
                  target: { kind: "ID", caseId },
                  reminderId: key,
                  expectedRevision: 9,
                }),
              ],
        values: [
          {
            kind: "TEXT",
            value: caption,
          },
        ],
      },
    ],
  },
});
const document: HumanReadDocument_case_get = {
  screen: {
    regions: [
      {
        role: "IDENTITY",
        content: {
          kind: "ENTRIES",
          entries: [entry("identity", "Core case identity Ω")],
        },
      },
      {
        role: "FACTS",
        titleId: "group.registration.label",
        content: {
          kind: "ENTRIES",
          entries: [entry("facts", "Core registration facts Ψ")],
        },
      },
      {
        role: "WORK",
        content: {
          kind: "ENTRIES",
          entries: [entry("work", "Core work caption Χ")],
        },
      },
      table("PAYMENTS", paymentId, "Core payment caption Φ"),
      table("NEXT_ACTIONS", reminderId, "Core next action caption Δ"),
    ],
  },
  subject: { kind: "CASE", caseId, revision: 4, workRevision: 6 },
  actions: [
    operationAffordance(
      "case.amendRegistration",
      { kind: "CASE", target: { kind: "ID", caseId }, expectedRevision: 4 },
      {
        ...prefixedInputs("field:", {
          handlingSummary: "Editable summary seed",
          responsibleActor: null,
        }),
      },
      ["field:handlingSummary"],
      {},
    ),
    operationAffordance(
      "work.update",
      { kind: "WORK", target: { kind: "ID", caseId }, expectedRevision: 6 },
      {
        ...prefixedInputs("field:", {
          handlingSummary: "Editable summary seed",
          responsibleActor: null,
        }),
      },
      ["field:handlingSummary"],
      {},
    ),
  ],
  links: [],
  history: "NONE",
  nextCursor: null,
  basis: {
    kind: "SNAPSHOT",
    observation: {
      observedAt: "2026-11-09T12:00:00.000000Z",
      businessDate: "2026-11-09",
      timeZone: "Etc/UTC",
    },
  },
  disclosure: "CURRENT",
};
const reply = (value: unknown) => ({
  representation: "HUMAN",
  outcome: {
    kind: "SUCCEEDED",
    document: value,
  },
});
afterEach(() => vi.unstubAllGlobals());
const caseView = (credential: string) => (
  <TestSession credential={credential}>
    <CaseDetail
      target={{
        kind: "ID",
        caseId,
      }}
      reloadSignal={0}
      definition={productMetadata.catalog}
      onBack={vi.fn()}
      onAct={vi.fn()}
    />
  </TestSession>
);
const mount = () => {
  const fetch = vi.fn<typeof globalThis.fetch>().mockResolvedValue(
    new Response(JSON.stringify(reply(document)), {
      status: 200,
      headers: {
        "Content-Type": "application/json",
      },
    }),
  );
  vi.stubGlobal("fetch", fetch);
  const onAct = vi.fn();
  const view = render(
    <CaseDetail
      target={{
        kind: "ID",
        caseId,
      }}
      reloadSignal={0}
      definition={productMetadata.catalog}
      onBack={vi.fn()}
      onAct={onAct}
    />,
  );
  return {
    onAct,
    fetch,
    view,
  };
};
it("renders Core case regions and binds offered edit/payment actions to typed controls", async () => {
  const f = mount();
  await screen.findByText("Core registration facts Ψ");
  for (const caption of [
    "Core case identity Ω",
    "Core work caption Χ",
    "Core payment caption Φ",
    "Core next action caption Δ",
  ]) {
    expect(screen.getByText(caption)).toBeVisible();
  }
  await userEvent.click(
    screen.getByRole("button", {
      name: /remove/iu,
    }),
  );
  expect(f.onAct).toHaveBeenCalledWith(
    expect.objectContaining<{
      subject: unknown;
    }>({
      subject: expect.objectContaining({
        caseId,
      }),
    }),
    "case.removePayment",
    paymentId,
  );
  expect(
    screen.queryByRole("button", {
      name: /change responsible/iu,
    }),
  ).not.toBeInTheDocument();
  expect(f.fetch).toHaveBeenCalledTimes(1);
});
it("opens work editing with only the permitted semantic summary seed and no saved read-only facts", async () => {
  mount();
  await screen.findByText("Core work caption Χ");
  await userEvent.click(
    screen.getByRole("button", {
      name: /edit summary/iu,
    }),
  );
  const input = screen.getByRole("textbox", {
    name: /handling summary/iu,
  });
  expect(input).toHaveValue("Editable summary seed");
  expect(
    screen.queryByRole("combobox", {
      name: /responsible/iu,
    }),
  ).not.toBeInTheDocument();
});
it("masks retained case content synchronously when the authenticated session changes", async () => {
  const fetch = vi
    .fn<typeof globalThis.fetch>()
    .mockResolvedValueOnce(
      new Response(JSON.stringify(reply(document)), {
        status: 200,
        headers: {
          "Content-Type": "application/json",
        },
      }),
    )
    .mockImplementation(
      () =>
        new Promise(() => {
          /* Keep the resource read pending until disclosure masks it. */
        }),
    );
  vi.stubGlobal("fetch", fetch);
  const view = render(caseView("owner-a"));
  await screen.findByText("Core registration facts Ψ");
  view.rerender(caseView("owner-b"));
  expect(screen.queryByText("Core registration facts Ψ")).not.toBeInTheDocument();
  expect(screen.queryByText("Core payment caption Φ")).not.toBeInTheDocument();
  await waitFor(() => {
    expect(fetch).toHaveBeenCalledTimes(2);
  });
});
it("omits retry for a case read whose supplied advice asks for access", async () => {
  const value = {
    representation: "HUMAN",
    outcome: {
      kind: "REJECTED",
      rejection: {
        kind: "REFUSED",
        advice: {
          message: {
            messageId: "diagnostic.RESOURCE_UNAVAILABLE",
            arguments: [],
          },
          action: "ASK_FOR_ACCESS",
        },
      },
    },
  };
  vi.stubGlobal(
    "fetch",
    vi.fn().mockResolvedValue(
      new Response(JSON.stringify(value), {
        status: 200,
        headers: {
          "Content-Type": "application/json",
        },
      }),
    ),
  );
  render(caseView("owner-a"));
  await screen.findByText("This item does not exist, or you do not have access to it.");
  expect(
    screen.queryByRole("button", {
      name: "Try again",
    }),
  ).toBeNull();
});
