import { afterEach, expect, it, vi } from "vitest";
import userEvent from "@testing-library/user-event";
import type { HumanReadDocument_caseGet, Region, PresentedEntry } from "@contracts/client";
import { isResponse } from "@contracts/client";
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
  value: { kind: "TEXT", value: caption },
});
const table = (role: Region["role"], key: string, caption: string): Region => ({
  role,
  content: {
    kind: "TABLE",
    columns: [{ key: "caption", labelId: "label.details", prominence: "PRIMARY", isMoney: false }],
    rows: [{ key, values: [{ kind: "TEXT", value: caption }] }],
  },
});
const document: HumanReadDocument_caseGet = {
  screen: {
    regions: [
      {
        role: "IDENTITY",
        content: { kind: "ENTRIES", entries: [entry("identity", "Core case identity Ω")] },
      },
      {
        role: "FACTS",
        titleId: "group.registration.label",
        content: { kind: "ENTRIES", entries: [entry("facts", "Core registration facts Ψ")] },
      },
      {
        role: "WORK",
        content: { kind: "ENTRIES", entries: [entry("work", "Core work caption Χ")] },
      },
      table("PAYMENTS", paymentId, "Core payment caption Φ"),
      table("NEXT_ACTIONS", reminderId, "Core next action caption Δ"),
    ],
  },
  context: {
    kind: "CASE",
    caseId,
    revision: 4,
    workRevision: 6,
    history: "NONE",
    tasks: ["CASE:AMEND_REGISTRATION", "CASE:REMOVE_PAYMENT", "WORK:UPDATE_WORK"],
    editableInputs: ["field:handlingSummary"],
    correction: null,
    seeds: {
      paymentCurrency: null,
      registration: null,
      decision: null,
      work: { summary: "Editable summary seed", responsible: null },
    },
    notes: [],
    payments: [{ paymentId, canRemove: true }],
    nextActions: [
      {
        caseId,
        reminderId,
        revision: 9,
        tasks: ["REMINDER:REMINDER_COMPLETE"],
        actionSeed: null,
        dueDateSeed: null,
      },
    ],
  },
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
  outcome: { kind: "SUCCEEDED", document: value },
});
afterEach(() => vi.unstubAllGlobals());
const caseView = (credential: string) => (
  <TestSession credential={credential}>
    <CaseDetail
      target={{ kind: "ID", caseId }}
      reloadSignal={0}
      definition={productMetadata.catalog}
      onBack={vi.fn()}
      onAct={vi.fn()}
    />
  </TestSession>
);
const mount = () => {
  expect(isResponse("case.get", reply(document))).toBe(true);
  const fetch = vi.fn().mockResolvedValue(
    new Response(JSON.stringify(reply(document)), {
      status: 200,
      headers: { "Content-Type": "application/json" },
    }),
  );
  vi.stubGlobal("fetch", fetch);
  const onAct = vi.fn();
  const view = render(
    <CaseDetail
      target={{ kind: "ID", caseId }}
      reloadSignal={0}
      definition={productMetadata.catalog}
      onBack={vi.fn()}
      onAct={onAct}
    />,
  );
  return { onAct, fetch, view };
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
  await userEvent.click(screen.getByRole("button", { name: /remove/i }));
  expect(f.onAct).toHaveBeenCalledWith(
    expect.objectContaining({ context: expect.objectContaining({ caseId }) }),
    "REMOVE_PAYMENT",
    paymentId,
  );
  expect(screen.queryByRole("button", { name: /change responsible/i })).not.toBeInTheDocument();
  expect(f.fetch).toHaveBeenCalledTimes(1);
});
it("opens work editing with only the permitted semantic summary seed and no saved read-only facts", async () => {
  mount();
  await screen.findByText("Core work caption Χ");
  await userEvent.click(screen.getByRole("button", { name: /edit summary/i }));
  const input = screen.getByRole("textbox", { name: /handling summary/i });
  expect(input).toHaveValue("Editable summary seed");
  expect(screen.queryByRole("combobox", { name: /responsible/i })).not.toBeInTheDocument();
});
it("refuses child handle mismatches and task escalation at the actual HUMAN boundary", () => {
  for (const context of [
    { ...document.context, payments: [] },
    { ...document.context, nextActions: [] },
    { ...document.context, tasks: ["WORK:UPDATE_WORK"] },
    {
      ...document.context,
      nextActions: [
        { ...document.context.nextActions[0], caseId: "00000000-0000-4000-8000-000000000099" },
      ],
    },
  ]) {
    expect(isResponse("case.get", reply({ ...document, context }))).toBe(false);
  }
});

it("masks retained case content synchronously when the authenticated session changes", async () => {
  const fetch = vi
    .fn()
    .mockResolvedValueOnce(
      new Response(JSON.stringify(reply(document)), {
        status: 200,
        headers: { "Content-Type": "application/json" },
      }),
    )
    .mockImplementation(() => new Promise(() => {}));
  vi.stubGlobal("fetch", fetch);
  const view = render(caseView("owner-a"));
  await screen.findByText("Core registration facts Ψ");
  view.rerender(caseView("owner-b"));
  expect(screen.queryByText("Core registration facts Ψ")).not.toBeInTheDocument();
  expect(screen.queryByText("Core payment caption Φ")).not.toBeInTheDocument();
  await waitFor(() => expect(fetch).toHaveBeenCalledTimes(2));
});
