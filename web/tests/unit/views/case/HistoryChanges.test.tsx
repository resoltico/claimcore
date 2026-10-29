import { expect, it, vi } from "vitest";
import userEvent from "@testing-library/user-event";
import { render, screen, waitFor } from "../../../support/presentation-test-support";
import { historyVariants } from "../../../support/contract-corpus.fixtures";
import { definition, fields, response } from "../../../support/service-ui.fixtures";
import type { CaseReceipt } from "../../../../src/api/service";
import { HistoryChanges } from "../../../../src/views/case/HistoryChanges";
import { CaseHistory } from "../../../../src/views/CaseHistory";
const receipt = (revision: number, claimant = "Earlier claimant"): CaseReceipt => ({
  ...historyVariants().full.receipt,
  operationId:
    revision === 1
      ? "10000000-0000-4000-8000-000000000001"
      : "20000000-0000-4000-8000-000000000002",
  subject: {
    ...historyVariants().full.receipt.subject,
    revision,
    act: revision === 1 ? "OPEN" : "AMEND_REGISTRATION",
    snapshot: {
      kind: "DISCLOSED",
      view: {
        kind: "ACTIVE",
        fields: {
          ...fields,
          registration: { ...fields.registration, claimantName: claimant },
        },
      },
    },
  },
});
it("compares only changed disclosed fields with canonical before and after values", () => {
  const view = render(
    <HistoryChanges
      receipt={receipt(2, "Current claimant")}
      previous={receipt(1)}
      fields={definition.catalog.fields}
    />,
  );
  const changes = view.container.querySelector(".history-changes")!;
  expect(changes.querySelectorAll("dt")).toHaveLength(1);
  expect(changes).toHaveTextContent("Earlier claimant");
  expect(changes).toHaveTextContent("Current claimant");
  expect(changes).not.toHaveTextContent(fields.registration.insurerName);
});
it("does not report unchanged fields as changes", () => {
  const view = render(
    <HistoryChanges
      receipt={receipt(2)}
      previous={receipt(1)}
      fields={definition.catalog.fields}
    />,
  );
  expect(view.container.querySelectorAll(".history-changes dt")).toHaveLength(0);
  expect(view.container).toHaveTextContent("No disclosed fields changed.");
});
it("uses no prior state for revision one and includes lifecycle and status", () => {
  const view = render(
    <HistoryChanges receipt={receipt(1)} previous={undefined} fields={definition.catalog.fields} />,
  );
  expect(view.container).toHaveTextContent("Before: Not recorded");
  expect(view.container).toHaveTextContent("Active");
  expect(view.container).toHaveTextContent("After: Open");
  expect(view.container.querySelectorAll(".history-changes dt").length).toBeGreaterThan(1);
});
it.each(["WITHHELD", "ERASED"] as const)(
  "does not invent before values from a %s prior snapshot",
  (kind) => {
    const previous = receipt(1);
    const view = render(
      <HistoryChanges
        receipt={receipt(2, "Visible claimant")}
        previous={{ ...previous, subject: { ...previous.subject, snapshot: { kind } } }}
        fields={definition.catalog.fields}
      />,
    );
    expect(view.container.querySelector(".history-changes")).toBeNull();
    expect(view.container).not.toHaveTextContent("Earlier claimant");
    expect(view.container).toHaveTextContent("cannot be compared");
  },
);
it("does not expose current withheld fields", () => {
  const current = receipt(2);
  const view = render(
    <HistoryChanges
      receipt={{ ...current, subject: { ...current.subject, snapshot: { kind: "WITHHELD" } } }}
      previous={receipt(1)}
      fields={definition.catalog.fields}
    />,
  );
  expect(view.container).toHaveTextContent("withheld");
  expect(view.container).not.toHaveTextContent("Earlier claimant");
});
it("loads the preceding bounded page using only the server-issued cursor when requested", async () => {
  const user = userEvent.setup();
  const fetch = vi
    .fn<typeof globalThis.fetch>()
    .mockResolvedValueOnce(
      response("case.history", "SUCCEEDED", {
        entries: [{ kind: "FULL", receipt: receipt(2, "Current claimant") }],
        nextCursor: "server-issued",
      }),
    )
    .mockResolvedValueOnce(
      response("case.history", "SUCCEEDED", {
        entries: [{ kind: "FULL", receipt: receipt(1) }],
        nextCursor: null,
      }),
    );
  vi.stubGlobal("fetch", fetch);
  const view = render(
    <CaseHistory caseReference="CASE-1" fields={definition.catalog.fields} modes={["FULL"]} />,
  );
  const heading = await screen.findByText(/revision 2/u);
  await user.click(heading);
  expect(fetch).toHaveBeenCalledTimes(1);
  const expand = view.container.querySelector(".history-list details button")!;
  await user.click(expand);
  await waitFor(() => {
    expect(view.container.querySelector(".history-changes")).toHaveTextContent("Current claimant");
  });
  expect(fetch).toHaveBeenCalledTimes(2);
  const body = fetch.mock.calls[1]?.[1]?.body;
  if (typeof body !== "string") {
    throw new Error("Expected JSON history request bytes.");
  }
  expect(JSON.parse(body)).toMatchObject({
    cursor: "server-issued",
    limit: 50,
    detail: "FULL",
  });
});
it("requires adjacent accepted revisions instead of comparing across missing history", () => {
  const view = render(
    <HistoryChanges
      receipt={receipt(3, "Current claimant")}
      previous={receipt(1)}
      fields={definition.catalog.fields}
    />,
  );
  expect(view.container.querySelector(".history-changes")).toBeNull();
  expect(view.container).toHaveTextContent("preceding accepted revision");
});
it("compares lifecycle and status independently from unchanged business facts", () => {
  const current = receipt(2);
  const view = render(
    <HistoryChanges
      receipt={{
        ...current,
        subject: {
          ...current.subject,
          snapshot: {
            kind: "DISCLOSED",
            view: {
              kind: "VOIDED",
              fields: {
                ...fields,
                status: "CLOSED",
                registration: { ...fields.registration, claimantName: "Earlier claimant" },
              },
            },
          },
        },
      }}
      previous={receipt(1)}
      fields={definition.catalog.fields}
    />,
  );
  expect(view.container).toHaveTextContent("Before: Active");
  expect(view.container).toHaveTextContent("After: Voided");
  expect(view.container.querySelectorAll(".history-changes dt")).toHaveLength(1);
  expect(view.container.querySelector(".history-changes")).toHaveTextContent("Closed");
});

it("retains erased disclosure without exposing prior fields as a fabricated removal diff", () => {
  const current = receipt(2);
  const view = render(
    <HistoryChanges
      receipt={{ ...current, subject: { ...current.subject, snapshot: { kind: "ERASED" } } }}
      previous={receipt(1)}
      fields={definition.catalog.fields}
    />,
  );
  expect(view.container).toHaveTextContent("Erased");
  expect(view.container.querySelector(".history-changes")).toBeNull();
  expect(view.container).not.toHaveTextContent("Earlier claimant");
});
