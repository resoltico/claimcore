import { expect, it, vi } from "vitest";
import userEvent from "@testing-library/user-event";
import { render, screen } from "../../../support/presentation-test-support";
import { historyVariants } from "../../../support/contract-corpus.fixtures";
import { definition, fields, response } from "../../../support/service-ui.fixtures";
import type { CaseReceipt } from "../../../../src/api/service";
import type { HistoryDifference } from "@contracts/client";
import { HistoryChanges } from "../../../../src/views/case/HistoryChanges";
import { CaseHistory } from "../../../../src/views/CaseHistory";
const receipt = (revision: number): CaseReceipt => ({
  ...historyVariants().full.receipt,
  operationId:
    revision === 1
      ? "10000000-0000-4000-8000-000000000001"
      : "20000000-0000-4000-8000-000000000002",
  subject: {
    ...historyVariants().full.receipt.subject,
    revision,
    act: revision === 1 ? "OPEN" : "AMEND_REGISTRATION",
    snapshot: { kind: "DISCLOSED", view: { kind: "ACTIVE", fields } },
  },
});
const difference: HistoryDifference = {
  lifecycle: null,
  fields: [{ field: "claimantName", before: "Core before", after: "Core after" }],
};
it("renders core-supplied differences without recomputing them from snapshots", () => {
  const view = render(
    <HistoryChanges
      receipt={receipt(2)}
      difference={difference}
      fields={definition.catalog.fields}
    />,
  );
  const changes = view.container.querySelector(".history-changes")!;
  expect(changes.querySelectorAll("dt")).toHaveLength(1);
  expect(changes).toHaveTextContent("Core before");
  expect(changes).toHaveTextContent("Core after");
  expect(changes).not.toHaveTextContent(fields.registration.claimantName);
});
it("renders the core's empty comparison result", () => {
  const view = render(
    <HistoryChanges
      receipt={receipt(2)}
      difference={{ lifecycle: null, fields: [] }}
      fields={definition.catalog.fields}
    />,
  );
  expect(view.container).toHaveTextContent("No disclosed fields changed.");
});
it("renders opening lifecycle and status supplied by core", () => {
  const view = render(
    <HistoryChanges
      receipt={receipt(1)}
      difference={{
        lifecycle: { before: null, after: "ACTIVE" },
        fields: [{ field: "status", before: null, after: "OPENED" }],
      }}
      fields={definition.catalog.fields}
    />,
  );
  expect(view.container).toHaveTextContent("Before: Not recorded");
  expect(view.container).toHaveTextContent("After: Active");
  expect(view.container).toHaveTextContent("After: Open");
});
it("retains unavailable comparisons without inventing earlier data", () => {
  const view = render(
    <HistoryChanges receipt={receipt(2)} difference={null} fields={definition.catalog.fields} />,
  );
  expect(view.container.querySelector(".history-changes")).toBeNull();
  expect(view.container).toHaveTextContent("cannot be compared");
});
it.each(["WITHHELD", "ERASED"] as const)(
  "a %s snapshot cannot expose supplied prior fields",
  (kind) => {
    const current = receipt(2);
    const view = render(
      <HistoryChanges
        receipt={{ ...current, subject: { ...current.subject, snapshot: { kind } } }}
        difference={difference}
        fields={definition.catalog.fields}
      />,
    );
    expect(view.container.querySelector(".history-changes")).toBeNull();
    expect(view.container).not.toHaveTextContent("Core before");
    expect(view.container).not.toHaveTextContent("Core after");
  },
);
it("shows page-boundary differences immediately and follows only the issued live continuation", async () => {
  const fetch = vi
    .fn<typeof globalThis.fetch>()
    .mockResolvedValueOnce(
      response("case.history", "SUCCEEDED", {
        entries: [{ kind: "FULL", receipt: receipt(2), difference }],
        nextCursor: "server-issued",
      }),
    )
    .mockResolvedValueOnce(
      response("case.history", "SUCCEEDED", {
        entries: [{ kind: "FULL", receipt: receipt(1), difference: null }],
        nextCursor: null,
      }),
    );
  vi.stubGlobal("fetch", fetch);
  const view = render(
    <CaseHistory caseReference="CASE-1" fields={definition.catalog.fields} modes={["FULL"]} />,
  );
  const user = userEvent.setup();
  await user.click(await screen.findByText(/revision 2/u));
  expect(view.container.querySelector(".history-changes")).toHaveTextContent("Core after");
  expect(fetch).toHaveBeenCalledTimes(1);
  await user.click(screen.getByRole("button", { name: "Load more history" }));
  expect(fetch).toHaveBeenCalledTimes(2);
  const body = fetch.mock.calls[1]?.[1]?.body;
  if (typeof body !== "string") {
    throw new Error("Expected JSON history bytes");
  }
  expect(JSON.parse(body)).toEqual({
    target: { kind: "REFERENCE", caseReference: "CASE-1" },
    detail: "FULL",
    cursor: "server-issued",
    limit: 50,
  });
});
